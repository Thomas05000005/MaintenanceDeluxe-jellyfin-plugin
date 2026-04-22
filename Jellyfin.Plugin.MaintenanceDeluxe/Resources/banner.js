(function () {
    "use strict";

    // Prevent double-execution (JS Injector + direct <script> tag).
    if (document.getElementById("jf-maintenance-deluxe")) return;

    var CONFIG = null; // loaded asynchronously from /MaintenanceDeluxe/config

    // --- Named constants ---
    var BANNER_Z_INDEX = 999999;
    var POPUP_Z_INDEX = 999998;
    var MOBILE_BREAKPOINT = 600;
    var POPUP_CLOSE_DELAY = 200; // ms to wait for fade-out before removing popup element
    var RESIZE_DEBOUNCE = 100;   // ms debounce on window resize before recomputing margin
    var NAV_DEBOUNCE = 50;       // ms debounce on SPA navigation events
    var SEL_SKIN_HEADER = ".skinHeader";
    var SEL_SKIN_BODY_PAGE = ".skinBody .page";

    var BANNER_H = 36;
    var BANNER_H_MOBILE = 42;
    var TRANSITION_MS = 300; // kept in sync with transitionSpeed after config load
    var rotationTimer = null;
    var dismissedMessages = new Set();
    var dismissAll = false;
    var permanentDismissed = false;
    var PERM_DISMISS_KEY = "__permanent__";
    var shuffledQueue = [];
    var currentMessage = null;
    var isPermanent = false;
    var isInPause = false;
    var skinHeaderObserver = null;
    var resizeTimer = null;
    var resizeHandler = null;
    var hideScrollObserver = null;
    var urlPopup = null;
    var _urlPopupOutside = null;
    var _urlPopupKey = null;

    var STORAGE_KEY = "jf-dismissed-v1";
    var CONFIG_LAST_MODIFIED = 0; // tracks the lastModified stamp of the loaded config

    var MAINTENANCE = null;
    var IS_ADMIN = false;
    var maintenanceOverlay = null;
    var adminDismissed = false;
    var maintenanceTimerId = null;
    var MD_STYLES_INJECTED = false;
    var OVERLAY_Z_INDEX = 1000000; // above BANNER_Z_INDEX (999999)

    // Cross-tab dismiss sync via BroadcastChannel (graceful degradation if unavailable).
    var bc = null;
    try { bc = new BroadcastChannel("jf-banner-v1"); } catch (e) {}

    function isAdminPage() {
        return /\b(dashboard|configurationpage|users|useredit|userprofiles|networking|devices|playback|dlna|notifications|libraries|metadata|subtitles|log|scheduledtasks|apikeys|activity|plugins|encodingsettings|streamingsettings)\b/.test(window.location.hash);
    }

    function checkTimeWindow(now, timeStart, timeEnd) {
        if (!timeStart && !timeEnd) return true;
        var nowMins = now.getHours() * 60 + now.getMinutes();
        if (timeStart) {
            var sp = timeStart.split(':');
            if (nowMins < parseInt(sp[0], 10) * 60 + parseInt(sp[1], 10)) return false;
        }
        if (timeEnd) {
            var ep = timeEnd.split(':');
            if (nowMins > parseInt(ep[0], 10) * 60 + parseInt(ep[1], 10)) return false;
        }
        return true;
    }

    function isInSchedule(msg) {
        var sch = msg.schedule;
        if (!sch || !sch.type || sch.type === 'always') return true;
        var now = new Date();
        if (sch.type === 'fixed') {
            if (sch.fixedStart) { var s = new Date(sch.fixedStart); if (isNaN(s.getTime()) || now < s) return false; }
            if (sch.fixedEnd) { var e = new Date(sch.fixedEnd); if (isNaN(e.getTime()) || now > e) return false; }
            return true;
        }
        if (sch.type === 'annual') {
            var ms = sch.monthStart, ds = sch.dayStart, me = sch.monthEnd, de = sch.dayEnd;
            if (!ms || !ds || !me || !de) return checkTimeWindow(now, sch.timeStart, sch.timeEnd);
            var nowMD = (now.getMonth() + 1) * 100 + now.getDate();
            var startMD = ms * 100 + ds, endMD = me * 100 + de;
            var inRange = startMD <= endMD
                ? nowMD >= startMD && nowMD <= endMD
                : nowMD >= startMD || nowMD <= endMD;
            return inRange && checkTimeWindow(now, sch.timeStart, sch.timeEnd);
        }
        if (sch.type === 'weekly') {
            if (!sch.weekDays || sch.weekDays.indexOf(now.getDay()) === -1) return false;
            return checkTimeWindow(now, sch.timeStart, sch.timeEnd);
        }
        if (sch.type === 'daily') {
            return checkTimeWindow(now, sch.timeStart, sch.timeEnd);
        }
        return true;
    }

    function matchesCurrentRoute(routes) {
        if (!routes || routes.length === 0) return true;
        var hash = window.location.hash.replace(/^#!?\/?/, '').split('?')[0];
        for (var i = 0; i < routes.length; i++) {
            if (routes[i] && routeGlobMatch(routes[i], hash)) return true;
        }
        return false;
    }

    function anyRoutesConfigured() {
        if (!CONFIG) return false;
        if ((CONFIG.rotationMessages || []).some(function (m) { return m.routes && m.routes.length; })) return true;
        var po = CONFIG.permanentOverride;
        if (po && (po.entries || []).some(function (e) { return e.routes && e.routes.length; })) return true;
        return false;
    }

    function routeGlobMatch(pattern, value) {
        var lp = pattern.toLowerCase();
        var lv = value.toLowerCase();
        var parts = lp.split('*');
        if (parts.length === 1) return lv === lp;
        var idx = 0;
        for (var p = 0; p < parts.length; p++) {
            var seg = parts[p];
            if (seg === '') continue;
            var found = lv.indexOf(seg, idx);
            if (found === -1) return false;
            if (p === 0 && found !== 0) return false;
            idx = found + seg.length;
        }
        var last = parts[parts.length - 1];
        if (last !== '' && !lv.endsWith(last)) return false;
        return true;
    }

    // --- Queue builder (shuffle or sequential based on config) ---
    function buildQueue() {
        var eligible = CONFIG.rotationMessages.filter(function (m) {
            return m.text && m.enabled !== false && isInSchedule(m) && matchesCurrentRoute(m.routes) && !dismissedMessages.has(m.text);
        });
        if (CONFIG.rotationShuffle !== false) {
            // Fisher-Yates shuffle
            for (var i = eligible.length - 1; i > 0; i--) {
                var j = Math.floor(Math.random() * (i + 1));
                var tmp = eligible[i]; eligible[i] = eligible[j]; eligible[j] = tmp;
            }
        }
        return eligible;
    }

    // --- Persist dismissed messages to localStorage ---
    function getPersistedDismissed() {
        try { return JSON.parse(localStorage.getItem(STORAGE_KEY) || "[]"); } catch (e) { return []; }
    }

    function savePersistedDismissed() {
        try {
            var arr = [];
            dismissedMessages.forEach(function (t) { arr.push(t); });
            localStorage.setItem(STORAGE_KEY, JSON.stringify(arr));
        } catch (e) { /* localStorage unavailable */ }
    }

    // --- CSS (uses CSS custom properties for config-driven values) ---
    var root = document.documentElement;
    var style = document.createElement("style");
    style.id = "jf-banner-style";
    style.textContent = [
        ":root {",
        "  --jf-h: " + BANNER_H + "px;",
        "  --jf-h-m: " + BANNER_H_MOBILE + "px;",
        "  --jf-tr: opacity .3s ease,transform .3s ease;",
        "  --jf-dur: .3s;",
        "  --jf-fs: 14px;",
        "  --jf-fs-m: 13px;",
        "}",
        "#jf-maintenance-deluxe {",
        "  position:fixed; top:0; left:0; width:100%; z-index:" + BANNER_Z_INDEX + ";",
        "  text-align:center; padding:0 70px; font-weight:bold; font-size:var(--jf-fs);",
        "  box-sizing:border-box; opacity:0; transform:translateY(-100%);",
        "  transition:var(--jf-tr);",
        "  display:flex; align-items:center; justify-content:center;",
        "  height:var(--jf-h);",
        "}",
        "#jf-maintenance-deluxe.visible { opacity:1; transform:translateY(0); }",
        "#jf-maintenance-deluxe.off { display:none!important; }",
        "#jf-banner-text { color:inherit; text-decoration:none; }",
        "@media(max-width:" + MOBILE_BREAKPOINT + "px){",
        "  #jf-maintenance-deluxe { font-size:var(--jf-fs-m); padding:0 36px; height:var(--jf-h-m); }",
        "  #jf-banner-dismiss-all { display:none!important; }",
        "  #jf-banner-close { font-size:22px; padding:4px 8px; }",
        "  #jf-banner-close-area { right:4px; }",
        "}",
        "#jf-banner-close-area {",
        "  position:absolute; right:8px; top:50%; transform:translateY(-50%);",
        "  display:flex; flex-direction:row; align-items:center; gap:6px;",
        "}",
        "#jf-banner-close {",
        "  background:none; border:none; font-size:18px; cursor:pointer;",
        "  opacity:.6; transition:opacity .2s; padding:0 4px; line-height:1;",
        "}",
        "#jf-banner-close:hover { opacity:1; }",
        "#jf-banner-dismiss-all {",
        "  background:none; border:none; font-size:9px; cursor:pointer;",
        "  opacity:.45; transition:opacity .2s; padding:0; line-height:1;",
        "  text-decoration:underline; white-space:nowrap;",
        "}",
        "#jf-banner-dismiss-all:hover { opacity:1; }",
        "#jf-maintenance-deluxe.permanent #jf-banner-close-area { display:none!important; }",
        "body.jf-banner-active .skinHeader { top:var(--jf-h)!important; transition:top .3s ease; }",
        "body.jf-banner-active .mainDrawer { top:var(--jf-h)!important; height:calc(100% - var(--jf-h))!important; transition:top .3s ease,height .3s ease; }",
        "@media(max-width:" + MOBILE_BREAKPOINT + "px){",
        "  body.jf-banner-active .skinHeader { top:var(--jf-h-m)!important; }",
        "  body.jf-banner-active .mainDrawer { top:var(--jf-h-m)!important; height:calc(100% - var(--jf-h-m))!important; }",
        "}",
        ".skinHeader,.mainDrawer,.skinBody { transition:top var(--jf-dur) ease,height var(--jf-dur) ease,padding-top var(--jf-dur) ease,margin-top var(--jf-dur) ease; }",
        "body.hide-scroll #jf-maintenance-deluxe { display:none!important; }",
        "body.hide-scroll .skinHeader { top:0!important; }",
        "body.hide-scroll .mainDrawer { top:0!important; height:100%!important; }",
        "body.hide-scroll .skinBody { padding-top:0!important; }",
        "#jf-url-popup {",
        "  position:fixed; z-index:" + POPUP_Z_INDEX + "; left:50%;",
        "  transform:translateX(-50%) translateY(-8px);",
        "  background:#1e1e1e; color:#e0e0e0;",
        "  border-radius:8px; padding:12px 14px;",
        "  box-shadow:0 6px 24px rgba(0,0,0,.6);",
        "  font-size:13px; font-weight:normal; font-family:inherit;",
        "  width:max-content; min-width:280px; max-width:min(480px, calc(100vw - 24px)); box-sizing:border-box;",
        "  opacity:0; transition:opacity .18s ease,transform .18s ease;",
        "}",
        "#jf-url-popup.jf-popup-in { opacity:1; transform:translateX(-50%) translateY(0); }",
        "#jf-url-popup-url { white-space:nowrap; overflow:hidden; text-overflow:ellipsis; margin-bottom:10px; opacity:.7; font-size:12px; line-height:1.4; }",
        "#jf-url-popup-hint { margin-bottom:10px; opacity:.85; font-size:12px; line-height:1.4; text-align:center; }",
        "#jf-url-popup-btns { display:flex; gap:8px; justify-content:center; align-items:center; }",
        ".jf-url-primary-btns { display:inline-grid; grid-template-columns:1fr 1fr; gap:8px; }",
        ".jf-url-btn { padding:7px 16px; border:none; border-radius:4px; cursor:pointer; font-size:13px; font-weight:600; line-height:1.4; white-space:nowrap; font-family:inherit; text-align:center; text-decoration:none; display:inline-block; box-sizing:border-box; }",
        ".jf-url-btn-open { background:#1976d2; color:#fff; }",
        ".jf-url-btn-copy { background:#333; color:#e0e0e0; }",
        ".jf-url-btn-cancel { background:none; color:#888; padding:7px 8px; }",
        "#jf-url-popup.jf-prefer-copy .jf-url-btn-open { background:#333; color:#e0e0e0; }",
        "#jf-url-popup.jf-prefer-copy .jf-url-btn-copy { background:#1976d2; color:#fff; }",
    ].join("\n");
    document.head.appendChild(style);

    // --- DOM ---
    var banner = document.createElement("div");
    banner.id = "jf-maintenance-deluxe";
    banner.classList.add("off");

    // textSpan is an <a> so it can optionally be a clickable link
    var textSpan = document.createElement("a");
    textSpan.id = "jf-banner-text";

    var closeArea = document.createElement("div");
    closeArea.id = "jf-banner-close-area";

    var closeBtn = document.createElement("button");
    closeBtn.id = "jf-banner-close";
    closeBtn.textContent = "\u2715";
    closeBtn.title = "Dismiss this announcement";
    closeBtn.addEventListener("click", dismissCurrent);

    var dismissAllBtn = document.createElement("button");
    dismissAllBtn.id = "jf-banner-dismiss-all";
    dismissAllBtn.title = "Hide all announcements for this session";
    dismissAllBtn.addEventListener("click", dismissAllMessages);

    closeArea.appendChild(dismissAllBtn);
    closeArea.appendChild(closeBtn);
    banner.appendChild(textSpan);
    banner.appendChild(closeArea);
    // NOTE: banner is NOT inserted into the DOM here.
    // It is inserted just before tick() runs (after the async config fetch),
    // so the Jellyfin SPA has finished mounting and won't remove it.

    // --- Actions ---
    function dismissCurrent() {
        if (isPermanent) {
            if (!CONFIG.permanentDismissible) return;
            permanentDismissed = true;
            if (CONFIG.persistDismiss) {
                dismissedMessages.add(PERM_DISMISS_KEY);
                savePersistedDismissed();
            }
            isPermanent = false;
            clearTimeout(rotationTimer);
            if (bc) bc.postMessage({ type: "dismissPermanent" });
            fadeOutThenNext();
            return;
        }
        if (!currentMessage) return;
        var dismissedText = currentMessage.text;
        dismissedMessages.add(dismissedText);
        if (CONFIG && CONFIG.persistDismiss) {
            savePersistedDismissed();
        }
        if (bc) bc.postMessage({ type: "dismiss", text: dismissedText });
        fadeOutThenNext();
    }

    function dismissAllMessages() {
        if (isPermanent) return;
        dismissAll = true;
        if (bc) bc.postMessage({ type: "dismissAll" });
        fadeOutThenHide();
    }

    // navigator.clipboard requires window.isSecureContext (HTTPS/localhost), which
    // excludes plain-HTTP self-hosted Jellyfin. Fall back to the legacy textarea
    // + execCommand path so copy works on Jellyfin Media Player and HTTP deployments.
    function copyToClipboard(text) {
        if (navigator.clipboard && navigator.clipboard.writeText && window.isSecureContext) {
            return navigator.clipboard.writeText(text)
                .then(function () { return true; })
                .catch(function () { return execCopy(text); });
        }
        return Promise.resolve(execCopy(text));
    }

    function execCopy(text) {
        var ta = document.createElement("textarea");
        ta.value = text;
        ta.setAttribute("readonly", "");
        ta.style.position = "fixed";
        ta.style.top = "0";
        ta.style.left = "0";
        ta.style.opacity = "0";
        document.body.appendChild(ta);
        ta.focus();
        ta.select();
        try { ta.setSelectionRange(0, ta.value.length); } catch (e) {}
        var ok = false;
        try { ok = document.execCommand("copy"); } catch (e) { ok = false; }
        ta.remove();
        return ok;
    }

    // Detect when we're running inside a Jellyfin mobile app WebView, where
    // opening an external URL is unreliable (Android often stays in-app; iOS may
    // block the navigation). In that case we surface "Copy URL" as the primary
    // action. Two-layered detection, because no single signal is bulletproof:
    //
    //   1. Canonical: window.NativeShell.AppHost.appName() is set by the official
    //      Jellyfin mobile apps to a string containing "Mobile". This catches
    //      iPad too, where navigator.userAgent defaults to Macintosh on iPadOS 13+
    //      and would otherwise slip through a UA sniff.
    //   2. Fallback heuristic for forks/older clients that don't expose an
    //      AppHost.appName: NativeShell present + mobile UA + touch support.
    //      Touch is what keeps Android TV (Jellyfin Android on a remote-controlled
    //      TV box) out of the "prefer copy" bucket.
    //
    // JMP on desktop exposes NativeShell too but fails layer 1 (appName is
    // "Jellyfin Desktop") and layer 2 (desktop UA, no touch).
    function isJellyfinMobileApp() {
        try {
            var ah = window.NativeShell && window.NativeShell.AppHost;
            if (ah && typeof ah.appName === "function") {
                var name = (ah.appName() || "").toLowerCase();
                if (name.indexOf("mobile") !== -1) return true;
            }
        } catch (_) { /* AppHost access threw — fall through to heuristic */ }
        if (!window.NativeShell) return false;
        var ua = navigator.userAgent || "";
        var mobileUa = /Android|iPhone|iPad|iPod/i.test(ua);
        var touch = "ontouchstart" in window || (navigator.maxTouchPoints || 0) > 0;
        return mobileUa && touch;
    }

    // --- URL popup: prevents WebView in-app navigation by never following the link ---
    function closeUrlPopup() {
        if (!urlPopup) return;
        var popup = urlPopup; urlPopup = null;
        if (_urlPopupOutside) { document.removeEventListener("click", _urlPopupOutside); _urlPopupOutside = null; }
        if (_urlPopupKey) { document.removeEventListener("keydown", _urlPopupKey); _urlPopupKey = null; }
        // Animate out (slide back up toward banner), then remove
        popup.classList.remove("jf-popup-in");
        var delay = TRANSITION_MS === 0 ? 0 : POPUP_CLOSE_DELAY;
        setTimeout(function () { if (popup.parentNode) popup.remove(); }, delay);
    }

    function showUrlPopup(url) {
        closeUrlPopup();
        var popup = document.createElement("div");
        popup.id = "jf-url-popup";
        if (isJellyfinMobileApp()) popup.classList.add("jf-prefer-copy");
        popup.style.top = ((window.innerWidth <= MOBILE_BREAKPOINT ? BANNER_H_MOBILE : BANNER_H) + 8) + "px";

        var urlDiv = document.createElement("div");
        urlDiv.id = "jf-url-popup-url";
        urlDiv.textContent = url;
        urlDiv.title = url;

        var hintText = CONFIG && typeof CONFIG.urlPopupHint === "string" ? CONFIG.urlPopupHint.trim() : "";
        var hintDiv = null;
        if (hintText) {
            hintDiv = document.createElement("div");
            hintDiv.id = "jf-url-popup-hint";
            hintDiv.textContent = hintText;
        }

        var btns = document.createElement("div");
        btns.id = "jf-url-popup-btns";

        function makeBtn(cls, label, fn) {
            var b = document.createElement("button");
            b.className = "jf-url-btn " + cls;
            b.textContent = label;
            b.addEventListener("click", fn);
            return b;
        }

        // Prefer Jellyfin's native shell bridge (JMP desktop + Jellyfin mobile apps
        // all expose window.NativeShell.openUrl → system browser). Fall through to
        // native <a target="_blank"> navigation on standard web browsers. window.open
        // alone is unreliable in JMP's Qt WebEngine (it reads currentHoveredUrl, which
        // is cleared when the mouse moves onto a non-link element like a <button>).
        var openBtn = document.createElement("a");
        openBtn.className = "jf-url-btn jf-url-btn-open";
        openBtn.textContent = "Open link";
        openBtn.href = url;
        openBtn.target = "_blank";
        openBtn.rel = "noopener noreferrer";
        openBtn.addEventListener("click", function (e) {
            if (window.NativeShell && typeof window.NativeShell.openUrl === "function") {
                e.preventDefault();
                try { window.NativeShell.openUrl(url); } catch (_) {}
            }
            closeUrlPopup();
        });

        var copyBtn = makeBtn("jf-url-btn-copy", "Copy URL", function () {
            copyToClipboard(url).then(function (ok) {
                copyBtn.textContent = ok ? "Copied!" : "Failed";
                if (ok) setTimeout(closeUrlPopup, 900);
            });
        });
        var cancelBtn = makeBtn("jf-url-btn-cancel", "Cancel", closeUrlPopup);

        var primaryBtns = document.createElement("div");
        primaryBtns.className = "jf-url-primary-btns";
        primaryBtns.appendChild(openBtn);
        primaryBtns.appendChild(copyBtn);
        btns.appendChild(primaryBtns);
        btns.appendChild(cancelBtn);
        popup.appendChild(urlDiv);
        if (hintDiv) popup.appendChild(hintDiv);
        popup.appendChild(btns);
        document.body.appendChild(popup);
        urlPopup = popup;

        // Animate in (slide down from banner)
        requestAnimationFrame(function () {
            requestAnimationFrame(function () { popup.classList.add("jf-popup-in"); });
        });

        // Close when clicking outside (deferred so this click doesn't immediately close it)
        setTimeout(function () {
            _urlPopupOutside = function (e) {
                if (!popup.contains(e.target)) closeUrlPopup();
            };
            document.addEventListener("click", _urlPopupOutside);
        }, 0);
        _urlPopupKey = function (e) { if (e.key === "Escape") closeUrlPopup(); };
        document.addEventListener("keydown", _urlPopupKey);
    }

    function fadeOutThenHide() {
        banner.classList.remove("visible");
        setTimeout(hideBanner, TRANSITION_MS);
    }

    function fadeOutThenNext() {
        banner.classList.remove("visible");
        setTimeout(function () {
            hideBanner();
            clearTimeout(rotationTimer);
            // Go to pause phase, not next message
            isInPause = true;
            var wait = CONFIG.pauseDuration > 0 ? CONFIG.pauseDuration * 1000 : 50;
            rotationTimer = setTimeout(tick, wait);
        }, TRANSITION_MS);
    }

    function showBanner(msg, permanent) {
        if (!msg || !msg.text) { hideBanner(); return; }
        if (!banner.isConnected) { document.body.prepend(banner); }
        currentMessage = msg;
        isPermanent = !!permanent;
        isInPause = false;

        textSpan.textContent = msg.text;
        if (textSpan._urlHandler) {
            textSpan.removeEventListener("click", textSpan._urlHandler);
            textSpan._urlHandler = null;
        }
        var safeUrl = /^(https?:\/\/|\/)/i;
        if (msg.url && safeUrl.test(msg.url)) {
            var targetUrl = msg.url;
            textSpan._urlHandler = function (e) {
                e.preventDefault();
                showUrlPopup(targetUrl);
            };
            textSpan.addEventListener("click", textSpan._urlHandler);
            textSpan.setAttribute("href", msg.url); // kept for right-click / copy-link on desktop
            textSpan.rel = "noopener noreferrer";
            textSpan.removeAttribute("target");
            textSpan.style.cursor = "pointer";
            textSpan.style.textDecoration = "underline";
        } else {
            textSpan.removeAttribute("href");
            textSpan.removeAttribute("target");
            textSpan.removeAttribute("rel");
            textSpan.style.cursor = "";
            textSpan.style.textDecoration = "";
        }
        banner.style.background = msg.bg || "#1976d2";
        banner.style.color = msg.color || "#fff";
        closeBtn.style.color = msg.color || "#fff";
        dismissAllBtn.style.color = msg.color || "#fff";

        banner.classList.remove("off");
        // Only add .permanent (which hides the close area via CSS) when not dismissible
        if (permanent && !CONFIG.permanentDismissible) banner.classList.add("permanent");
        else banner.classList.remove("permanent");
        // Hide "dismiss all" for permanent banners; show it for rotation
        dismissAllBtn.style.display =
            (permanent && CONFIG.permanentDismissible) || CONFIG.showDismissAll === false
                ? 'none' : '';
        closeBtn.style.display = CONFIG.showDismissButton === false ? 'none' : '';
        if (CONFIG.dismissButtonSize) closeBtn.style.fontSize = CONFIG.dismissButtonSize + "px";
        if (CONFIG.dismissAllSize) dismissAllBtn.style.fontSize = CONFIG.dismissAllSize + "px";
        document.body.classList.add("jf-banner-active");

        // Set up observers to recompute margin when layout changes.
        // Always disconnect first: on rapid hide/show the previous observer may
        // be attached to a stale element that is no longer in the DOM.
        var sh = document.querySelector(SEL_SKIN_HEADER);
        if (skinHeaderObserver) { skinHeaderObserver.disconnect(); skinHeaderObserver = null; }
        if (sh) {
            skinHeaderObserver = new ResizeObserver(function () { applyBodyMargin(); });
            skinHeaderObserver.observe(sh);
        }
        // Recompute when viewport resizes (catches the MOBILE_BREAKPOINT where B changes
        // but skinHeader height may not, so ResizeObserver alone would miss it).
        if (!resizeHandler) {
            resizeHandler = function () {
                clearTimeout(resizeTimer);
                resizeTimer = setTimeout(applyBodyMargin, RESIZE_DEBOUNCE);
            };
            window.addEventListener('resize', resizeHandler);
        }
        // Remove inline padding-top when Jellyfin hides the page for the video player,
        // restore it when the player closes.
        if (!hideScrollObserver) {
            hideScrollObserver = new MutationObserver(function () {
                if (document.body.classList.contains('hide-scroll')) {
                    document.querySelectorAll(SEL_SKIN_BODY_PAGE).forEach(function (el) {
                        el.style.removeProperty('padding-top');
                    });
                } else if (document.body.classList.contains('jf-banner-active')) {
                    requestAnimationFrame(applyBodyMargin);
                }
            });
            hideScrollObserver.observe(document.body, { attributes: true, attributeFilter: ['class'] });
        }
        requestAnimationFrame(function () { applyBodyMargin(); });

        requestAnimationFrame(function () {
            requestAnimationFrame(function () {
                banner.classList.add("visible");
            });
        });
    }

    function hideBanner() {
        closeUrlPopup();
        currentMessage = null;
        banner.classList.remove("visible");
        banner.classList.add("off");
        banner.classList.remove("permanent");
        document.body.classList.remove("jf-banner-active");
        clearBodyMargin();
    }

    function applyBodyMargin() {
        var sh = document.querySelector(SEL_SKIN_HEADER);
        if (!sh) return;
        var B = window.innerWidth <= MOBILE_BREAKPOINT ? BANNER_H_MOBILE : BANNER_H;
        // .page elements are position:absolute at top:0 inside a fixed container —
        // skinBody margin-top does not move them. Set padding-top to exactly where
        // the header's bottom edge sits (banner height + current header height).
        // Read phase: all getBoundingClientRect() calls before any writes to avoid
        // forced reflows inside the loop.
        var shBottom = (B + sh.getBoundingClientRect().height) + 'px';
        var pages = document.querySelectorAll(SEL_SKIN_BODY_PAGE);
        // Write phase: apply in a single pass after all reads are done.
        for (var i = 0; i < pages.length; i++) {
            pages[i].style.setProperty('padding-top', shBottom, 'important');
        }
    }

    function clearBodyMargin() {
        document.querySelectorAll(SEL_SKIN_BODY_PAGE).forEach(function (el) {
            el.style.removeProperty('padding-top');
        });
        if (skinHeaderObserver) { skinHeaderObserver.disconnect(); skinHeaderObserver = null; }
        if (hideScrollObserver) { hideScrollObserver.disconnect(); hideScrollObserver = null; }
        if (resizeHandler) {
            clearTimeout(resizeTimer);
            window.removeEventListener('resize', resizeHandler);
            resizeHandler = null;
        }
    }

    // --- Maintenance overlay (premium velours + aurora + glass + countdown) ---

    function isLikelyTV() {
        var ua = navigator.userAgent || "";
        return /Tizen|Web0S|webOS|AFT[A-Z]|CrKey/i.test(ua)
            || /Android.*TV|BRAVIA|SHIELD|NVIDIA Shield/i.test(ua);
    }
    function prefersReducedMotion() {
        try { return matchMedia("(prefers-reduced-motion: reduce)").matches; } catch (e) { return false; }
    }
    function getPerfTier() {
        if (prefersReducedMotion()) return "minimal";
        if (isLikelyTV()) return "reduced";
        return "full";
    }

    function injectMaintenanceStyles() {
        if (MD_STYLES_INJECTED) return;
        MD_STYLES_INJECTED = true;
        var style = document.createElement("style");
        style.id = "jf-md-styles";
        style.textContent = MD_CSS;
        document.head.appendChild(style);
    }

    // Minimal safe-subset markdown -> HTML: **bold**, *italic*, lines starting with "-" become <ul><li>.
    function mdToHtml(src) {
        if (!src) return "";
        var esc = function (s) { return s.replace(/[&<>"']/g, function (c) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
        }); };
        function renderInline(s) {
            return s.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
                    .replace(/\*([^*]+)\*/g, "<em>$1</em>");
        }
        var lines = src.split(/\r?\n/);
        var html = "";
        var inList = false;
        for (var i = 0; i < lines.length; i++) {
            var line = lines[i];
            var trimmed = line.replace(/^\s+/, "");
            var isItem = /^-\s+/.test(trimmed);
            if (isItem) {
                if (!inList) { html += "<ul>"; inList = true; }
                html += "<li>" + renderInline(esc(trimmed.replace(/^-\s+/, ""))) + "</li>";
            } else {
                if (inList) { html += "</ul>"; inList = false; }
                if (trimmed.length === 0) { if (i > 0) html += "<br>"; }
                else html += "<p>" + renderInline(esc(line)) + "</p>";
            }
        }
        if (inList) html += "</ul>";
        return html;
    }

    function formatLocalTime(date) {
        try {
            return date.toLocaleTimeString(navigator.language || "default", {
                hour: "2-digit", minute: "2-digit", hour12: false
            }).replace(":", "h");
        } catch (e) {
            return date.getHours() + "h" + String(date.getMinutes()).padStart(2, "0");
        }
    }

    function formatRelative(ms) {
        if (ms <= 0) return "";
        var totalSec = Math.floor(ms / 1000);
        var h = Math.floor(totalSec / 3600);
        var m = Math.floor((totalSec % 3600) / 60);
        var s = totalSec % 60;
        if (totalSec < 5 * 60) {
            return (m > 0 ? m + " min " : "") + String(s).padStart(2, "0") + " s";
        }
        if (h >= 1) return "≈ " + h + " h " + (m > 0 ? m + " min" : "");
        return "≈ " + m + " min";
    }

    function parseDateOrNull(s) {
        if (!s) return null;
        var d = new Date(s);
        return isNaN(d.getTime()) ? null : d;
    }

    function updateMaintenanceTimer() {
        if (!maintenanceOverlay || !MAINTENANCE) return;
        var absEl = maintenanceOverlay.querySelector(".jf-md-time-absolute");
        var relEl = maintenanceOverlay.querySelector(".jf-md-time-relative");
        var timeBox = maintenanceOverlay.querySelector(".jf-md-time");
        var fill = maintenanceOverlay.querySelector(".jf-md-progress-fill");
        if (!absEl || !relEl || !timeBox || !fill) return;

        var start = parseDateOrNull(MAINTENANCE.scheduledStart);
        var activatedAt = parseDateOrNull(MAINTENANCE.activatedAt);
        var end = parseDateOrNull(MAINTENANCE.scheduledEnd);
        if (!end) {
            absEl.textContent = "";
            relEl.textContent = "Retour bientôt";
            fill.style.width = "100%";
            timeBox.querySelector(".jf-md-progress").style.display = "none";
            return;
        }

        var now = Date.now();
        var endMs = end.getTime();
        // startMs preference: scheduledStart > activatedAt (set server-side on activation) > now as last resort.
        var startMs = start ? start.getTime()
                            : activatedAt ? activatedAt.getTime()
                            : now;
        var total = Math.max(1, endMs - startMs);
        var elapsed = now - startMs;
        var remaining = endMs - now;

        absEl.textContent = "Retour prévu à " + formatLocalTime(end);

        if (remaining > 0) {
            timeBox.classList.remove("overtime");
            relEl.textContent = formatRelative(remaining);
            fill.style.width = Math.min(100, Math.max(0, (elapsed / total) * 100)) + "%";
        } else {
            timeBox.classList.add("overtime");
            var overM = Math.floor(-remaining / 60000);
            relEl.textContent = "Finalisation en cours" + (overM > 0 ? " (+" + overM + " min)" : "");
            fill.style.width = "100%";
        }
    }

    function escapeMdHtml(s) {
        return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
        });
    }

    function buildMaintenanceOverlay(m, isAdmin) {
        var tier = getPerfTier();
        var title = (m && m.customTitle && m.customTitle.trim()) || "Serveur en maintenance";
        var subtitle = (m && m.customSubtitle && m.customSubtitle.trim()) || "On en profite pour améliorer ton expérience";
        var message = (m && m.message) || "";
        var statusUrl = (m && m.statusUrl) || "";
        var notes = (m && m.releaseNotes) || [];

        var overlay = document.createElement("div");
        overlay.id = "jf-md-overlay";
        overlay.className = "jf-md-tier-" + tier;
        overlay.setAttribute("role", "status");
        overlay.setAttribute("aria-live", "polite");

        var bg = document.createElement("div");
        bg.className = "jf-md-bg";
        bg.innerHTML =
            '<div class="jf-md-blob jf-md-blob--gold"></div>' +
            '<div class="jf-md-blob jf-md-blob--midnight"></div>' +
            '<div class="jf-md-grain"></div>';
        overlay.appendChild(bg);

        if (tier === "full") {
            var particles = document.createElement("div");
            particles.className = "jf-md-particles";
            for (var i = 0; i < 10; i++) {
                var p = document.createElement("span");
                p.className = "jf-md-particle";
                p.style.left = (Math.random() * 100) + "%";
                p.style.top = (Math.random() * 100) + "%";
                p.style.animationDuration = (14 + Math.random() * 10) + "s";
                p.style.animationDelay = (-Math.random() * 20) + "s";
                particles.appendChild(p);
            }
            overlay.appendChild(particles);
        }

        var card = document.createElement("div");
        card.className = "jf-md-card";

        var cardHtml =
            '<div class="jf-md-logo" aria-hidden="true">▶</div>' +
            '<h1 class="jf-md-title">' + escapeMdHtml(title) + '</h1>' +
            '<p class="jf-md-subtitle">' + escapeMdHtml(subtitle) + '</p>';

        if (message && message.trim() && message.trim() !== subtitle.trim()) {
            cardHtml += '<p class="jf-md-message">' + escapeMdHtml(message) + '</p>';
        }

        cardHtml +=
            '<div class="jf-md-time" aria-hidden="true">' +
                '<div class="jf-md-time-absolute"></div>' +
                '<div class="jf-md-time-relative"></div>' +
                '<div class="jf-md-progress"><div class="jf-md-progress-fill"></div></div>' +
            '</div>';

        if (notes.length > 0) {
            cardHtml += '<div class="jf-md-notes">' +
                '<h2 class="jf-md-notes-title">Au programme</h2>' +
                '<div class="jf-md-notes-list">';
            for (var j = 0; j < notes.length; j++) {
                var n = notes[j];
                cardHtml += '<div class="jf-md-note">' +
                    '<div class="jf-md-note-icon">' + escapeMdHtml(n.icon || "✨") + '</div>' +
                    '<div class="jf-md-note-content">' +
                        (n.title ? '<h3 class="jf-md-note-title">' + escapeMdHtml(n.title) + '</h3>' : '') +
                        (n.body ? '<div class="jf-md-note-body">' + mdToHtml(n.body) + '</div>' : '') +
                    '</div>' +
                '</div>';
            }
            cardHtml += '</div></div>';
        }

        cardHtml += '<div class="jf-md-footer">';
        if (statusUrl) {
            cardHtml += '<a class="jf-md-status-link" href="' + encodeURI(statusUrl) + '" target="_blank" rel="noopener noreferrer">Voir le statut détaillé ↗</a>';
        }
        if (isAdmin) {
            cardHtml += '<button type="button" class="jf-md-dismiss">✕ Accès admin</button>';
        }
        cardHtml += '</div>';

        card.innerHTML = cardHtml;
        overlay.appendChild(card);

        if (isAdmin) {
            var dismissBtn = card.querySelector(".jf-md-dismiss");
            if (dismissBtn) {
                dismissBtn.addEventListener("click", function () {
                    adminDismissed = true;
                    removeMaintenanceOverlay();
                });
            }
        }

        return overlay;
    }

    function showMaintenanceOverlay(m, isAdmin) {
        if (maintenanceOverlay) return;
        injectMaintenanceStyles();
        var overlay = buildMaintenanceOverlay(m, isAdmin);
        document.body.appendChild(overlay);
        maintenanceOverlay = overlay;
        updateMaintenanceTimer();
        requestAnimationFrame(function () { overlay.classList.add("visible"); });
        if (maintenanceTimerId) clearInterval(maintenanceTimerId);
        maintenanceTimerId = setInterval(updateMaintenanceTimer, 1000);
    }

    function removeMaintenanceOverlay() {
        if (maintenanceTimerId) { clearInterval(maintenanceTimerId); maintenanceTimerId = null; }
        if (maintenanceOverlay && maintenanceOverlay.parentNode) {
            maintenanceOverlay.parentNode.removeChild(maintenanceOverlay);
        }
        maintenanceOverlay = null;
    }

    function applyMaintenanceState() {
        if (!MAINTENANCE) return;
        if (MAINTENANCE.isActive) {
            if (!maintenanceOverlay && !(IS_ADMIN && adminDismissed)) {
                showMaintenanceOverlay(MAINTENANCE, IS_ADMIN);
            }
        } else {
            adminDismissed = false;
            removeMaintenanceOverlay();
        }
    }

    // Premium overlay stylesheet — injected once on first show.
    var MD_CSS = [
        "#jf-md-overlay{position:fixed;inset:0;z-index:" + OVERLAY_Z_INDEX + ";",
        "background:#1A1412;color:#F4EDE4;",
        "font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;",
        "overflow:hidden;display:flex;align-items:center;justify-content:center;padding:24px;",
        "opacity:0;transition:opacity .4s ease;}",
        "#jf-md-overlay.visible{opacity:1;}",
        ".jf-md-bg{position:absolute;inset:0;pointer-events:none;overflow:hidden;}",
        ".jf-md-blob{position:absolute;border-radius:50%;filter:blur(120px);opacity:.22;will-change:transform;}",
        ".jf-md-blob--gold{width:60vw;height:60vw;top:-20%;left:-10%;",
        "background:radial-gradient(circle,#C9A96E 0%,transparent 70%);",
        "animation:jf-md-drift1 80s ease-in-out infinite alternate;}",
        ".jf-md-blob--midnight{width:55vw;height:55vw;bottom:-25%;right:-15%;",
        "background:radial-gradient(circle,#1e2a42 0%,transparent 70%);",
        "animation:jf-md-drift2 100s ease-in-out infinite alternate;}",
        "@keyframes jf-md-drift1{to{transform:translate(12vw,10vh) scale(1.08);}}",
        "@keyframes jf-md-drift2{to{transform:translate(-10vw,-8vh) scale(.95);}}",
        ".jf-md-grain{position:absolute;inset:0;pointer-events:none;opacity:.05;mix-blend-mode:overlay;",
        "background-image:url(\"data:image/svg+xml;utf8,<svg viewBox='0 0 200 200' xmlns='http://www.w3.org/2000/svg'><filter id='n'><feTurbulence type='fractalNoise' baseFrequency='0.9' numOctaves='2' stitchTiles='stitch'/></filter><rect width='100%25' height='100%25' filter='url(%23n)'/></svg>\");}",
        ".jf-md-particles{position:absolute;inset:0;pointer-events:none;}",
        ".jf-md-particle{position:absolute;width:3px;height:3px;background:#C9A96E;border-radius:50%;",
        "opacity:.4;box-shadow:0 0 6px #C9A96E;will-change:transform;",
        "animation:jf-md-float 18s linear infinite;}",
        "@keyframes jf-md-float{",
        "0%{transform:translate(0,0);opacity:0;}",
        "10%{opacity:.4;}",
        "90%{opacity:.4;}",
        "100%{transform:translate(20px,-100vh);opacity:0;}}",
        ".jf-md-card{position:relative;max-width:640px;width:100%;max-height:92vh;overflow-y:auto;",
        "padding:44px 40px;border-radius:16px;background:rgba(36,28,24,.55);",
        "border:1px solid rgba(201,169,110,.18);text-align:center;",
        "box-shadow:0 0 0 1px rgba(255,255,255,.02) inset,",
        "0 20px 60px -20px rgba(201,169,110,.25),",
        "0 0 40px -10px rgba(201,169,110,.15);",
        "backdrop-filter:blur(20px) saturate(1.2);",
        "-webkit-backdrop-filter:blur(20px) saturate(1.2);}",
        "@supports not ((backdrop-filter:blur(1px)) or (-webkit-backdrop-filter:blur(1px))){",
        ".jf-md-card{background:rgba(36,28,24,.95);}}",
        ".jf-md-logo{font-size:36px;margin-bottom:20px;color:#C9A96E;",
        "animation:jf-md-breathe 5s ease-in-out infinite;}",
        "@keyframes jf-md-breathe{0%,100%{opacity:.55;}50%{opacity:1;}}",
        ".jf-md-title{font-family:'Instrument Serif','Cormorant Garamond',Georgia,serif;",
        "font-weight:300;font-size:clamp(28px,5vw,42px);color:#C9A96E;",
        "margin:0 0 8px 0;letter-spacing:.02em;line-height:1.15;}",
        ".jf-md-subtitle{font-size:16px;color:#A89584;margin:0 0 16px 0;line-height:1.5;}",
        ".jf-md-message{font-size:14px;color:#A89584;margin:12px 0 0;line-height:1.5;font-style:italic;opacity:.85;}",
        ".jf-md-time{margin:28px 0 20px;padding:18px;border-radius:10px;",
        "background:rgba(0,0,0,.25);border:1px solid rgba(201,169,110,.12);}",
        ".jf-md-time-absolute{font-family:'Geist Mono','JetBrains Mono',Menlo,Consolas,monospace;",
        "font-variant-numeric:tabular-nums;font-size:clamp(22px,4vw,30px);",
        "font-weight:400;color:#F4EDE4;line-height:1.2;}",
        ".jf-md-time-relative{margin-top:4px;font-size:14px;color:#A89584;font-variant-numeric:tabular-nums;}",
        ".jf-md-progress{margin-top:14px;height:4px;background:rgba(201,169,110,.12);border-radius:2px;overflow:hidden;}",
        ".jf-md-progress-fill{height:100%;background:linear-gradient(90deg,#C9A96E,#8B4A3A);transition:width 1s linear;width:0;}",
        ".jf-md-time.overtime .jf-md-progress-fill{background:#d18033;animation:jf-md-pulse 2s ease-in-out infinite;}",
        "@keyframes jf-md-pulse{50%{opacity:.45;}}",
        ".jf-md-notes{margin-top:26px;padding-top:22px;border-top:1px solid rgba(201,169,110,.15);}",
        ".jf-md-notes-title{font-size:11px;text-transform:uppercase;letter-spacing:.18em;",
        "color:#A89584;font-weight:500;margin:0 0 14px 0;text-align:left;}",
        ".jf-md-note{display:grid;grid-template-columns:36px 1fr;gap:12px;margin-bottom:14px;text-align:left;}",
        ".jf-md-note-icon{font-size:22px;line-height:1.2;}",
        ".jf-md-note-title{font-size:14.5px;color:#C9A96E;margin:0 0 4px 0;font-weight:500;}",
        ".jf-md-note-body{font-size:13px;color:#A89584;line-height:1.6;}",
        ".jf-md-note-body p{margin:0 0 4px;}",
        ".jf-md-note-body strong{color:#F4EDE4;font-weight:600;}",
        ".jf-md-note-body em{font-style:italic;}",
        ".jf-md-note-body ul{padding-left:18px;margin:4px 0;}",
        ".jf-md-note-body li{margin:2px 0;}",
        ".jf-md-footer{margin-top:22px;padding-top:18px;border-top:1px solid rgba(201,169,110,.12);",
        "display:flex;gap:12px;align-items:center;justify-content:center;flex-wrap:wrap;}",
        ".jf-md-status-link{color:#C9A96E;font-size:13px;text-decoration:none;padding:7px 14px;",
        "border-radius:6px;border:1px solid rgba(201,169,110,.25);transition:background .2s;}",
        ".jf-md-status-link:hover{background:rgba(201,169,110,.1);}",
        ".jf-md-dismiss{padding:7px 14px;border-radius:6px;",
        "border:1px solid rgba(255,255,255,.15);background:rgba(255,255,255,.05);",
        "color:#A89584;cursor:pointer;font-size:12px;font-family:inherit;}",
        ".jf-md-dismiss:hover{background:rgba(255,255,255,.1);}",
        "@media (max-width:600px){.jf-md-card{padding:32px 22px;border-radius:12px;}}",
        ".jf-md-tier-reduced .jf-md-blob,",
        ".jf-md-tier-reduced .jf-md-logo,",
        ".jf-md-tier-reduced .jf-md-time.overtime .jf-md-progress-fill{animation:none!important;}",
        ".jf-md-tier-reduced .jf-md-particles{display:none;}",
        ".jf-md-tier-reduced .jf-md-progress-fill{transition:none;}",
        ".jf-md-tier-minimal .jf-md-bg{display:none;}",
        ".jf-md-tier-minimal .jf-md-particles{display:none;}",
        ".jf-md-tier-minimal .jf-md-card{backdrop-filter:none;-webkit-backdrop-filter:none;background:rgba(36,28,24,.98);}",
        ".jf-md-tier-minimal *{animation:none!important;transition:none!important;}"
    ].join("");

    // --- Main loop ---
    function tick() {
        if (CONFIG.showInDashboard === false && isAdminPage()) { hideBanner(); return; }

        // Permanent override
        var po = CONFIG.permanentOverride;
        if (po && po.enabled !== false && po.activeIndex >= 0 && !permanentDismissed) {
            var entry = po.entries && po.entries[po.activeIndex];
            if (entry && entry.text && isInSchedule(entry) && matchesCurrentRoute(entry.routes)) {
                showBanner(entry, true);
                rotationTimer = setTimeout(tick, CONFIG.displayDuration * 1000);
                return;
            }
        }

        if (dismissAll || CONFIG.rotationEnabled === false) { hideBanner(); return; }

        // Currently showing a message → go to pause
        if (!isInPause && currentMessage) {
            banner.classList.remove("visible");
            setTimeout(hideBanner, TRANSITION_MS);
            isInPause = true;
            if (CONFIG.pauseDuration > 0) {
                rotationTimer = setTimeout(tick, CONFIG.pauseDuration * 1000);
            } else {
                rotationTimer = setTimeout(tick, 50);
            }
            return;
        }

        // In pause or first run → pick next message
        isInPause = false;

        if (shuffledQueue.length === 0) {
            shuffledQueue = buildQueue();
            if (shuffledQueue.length === 0) { hideBanner(); return; }
        }

        var msg = shuffledQueue.shift();

        // Re-check in case schedule/dismiss/route changed
        if (!msg || !msg.text || !isInSchedule(msg) || !matchesCurrentRoute(msg.routes) || dismissedMessages.has(msg.text)) {
            // Try next in queue immediately
            rotationTimer = setTimeout(tick, 50);
            return;
        }

        showBanner(msg, false);
        rotationTimer = setTimeout(tick, CONFIG.displayDuration * 1000);
    }

    // --- Go ---
    function getToken() { return window.ApiClient ? window.ApiClient.accessToken() : null; }

    // Preview mode short-circuit: ?md-preview=1 renders the overlay with mock data
    // so admins can iterate the design without activating maintenance for real users.
    function isPreviewMode() {
        try { return new URLSearchParams(window.location.search).get("md-preview") === "1"; }
        catch (e) { return false; }
    }

    function mockMaintenance() {
        var now = new Date();
        var start = new Date(now.getTime() - 20 * 60 * 1000);  // activated 20 min ago
        var end = new Date(now.getTime() + 35 * 60 * 1000);    // ends in 35 min
        return {
            isActive: true,
            customTitle: "Serveur en maintenance",
            customSubtitle: "On en profite pour améliorer ton expérience",
            message: "",
            statusUrl: "",
            activatedAt: start.toISOString(),
            scheduledStart: start.toISOString(),
            scheduledEnd: end.toISOString(),
            scheduledRestart: null,
            scheduleEnabled: true,
            releaseNotes: [
                {
                    icon: "🎬",
                    title: "12 nouveaux films ajoutés",
                    body: "Ajout des dernières sorties : **Dune 3**, **Oppenheimer**, et le meilleur de A24.\n\nParfait pour une soirée cinéma."
                },
                {
                    icon: "⚡",
                    title: "Jellyfin 10.11.6 → 10.12",
                    body: "- Streaming **30% plus rapide** (nouveau décodeur)\n- Meilleure compression vidéo\n- Correction de 47 bugs"
                },
                {
                    icon: "🧹",
                    title: "Nettoyage de la bibliothèque",
                    body: "Suppression des doublons + recompression des 4K trop lourds. *~80 Go récupérés.*"
                }
            ]
        };
    }

    if (isPreviewMode()) {
        MAINTENANCE = mockMaintenance();
        IS_ADMIN = true;
        applyMaintenanceState();
        // Visual "PREVIEW" badge so admin can't confuse it with real maintenance.
        setTimeout(function () {
            var overlay = document.getElementById("jf-md-overlay");
            if (!overlay) return;
            var badge = document.createElement("div");
            badge.style.cssText = [
                "position:absolute;top:16px;right:16px;z-index:10;",
                "padding:6px 14px;border-radius:999px;",
                "background:rgba(209,128,51,.18);border:1px solid rgba(209,128,51,.55);",
                "color:#d18033;font-size:10px;font-weight:600;letter-spacing:.18em;",
                "text-transform:uppercase;font-family:'Geist Mono',monospace;"
            ].join("");
            badge.textContent = "Prévisualisation";
            overlay.appendChild(badge);
        }, 120);
        return;
    }

    // Fetch maintenance state without auth — works even on the login page.
    fetch("/MaintenanceDeluxe/maintenance")
        .then(function (r) { return r.ok ? r.json() : null; })
        .catch(function () { return null; })
        .then(function (maint) {
            MAINTENANCE = maint;

            var token = getToken();
            if (!token) {
                // Login page / no session — show overlay if active, then stop.
                applyMaintenanceState();
                return;
            }

            var configPromise = fetch("/MaintenanceDeluxe/config", {
                headers: { "Authorization": "MediaBrowser Token=\"" + token + "\"" }
            }).then(function (r) { return r.ok ? r.json() : null; });

            var userPromise = (window.ApiClient && typeof window.ApiClient.getCurrentUser === "function")
                ? window.ApiClient.getCurrentUser() : Promise.resolve(null);

            Promise.all([configPromise, userPromise])
                .then(function (results) {
                    var config = results[0]; var user = results[1];
                    if (!config) return;
                    CONFIG = config;
                    CONFIG_LAST_MODIFIED = config.lastModified || 0;
                    IS_ADMIN = !!(user && user.Policy && user.Policy.IsAdministrator);

            // --- Banner height ---
            var h = Math.max(24, Math.min(80, CONFIG.bannerHeight || BANNER_H));
            var hm = h + 6;
            root.style.setProperty("--jf-h", h + "px");
            root.style.setProperty("--jf-h-m", hm + "px");
            BANNER_H = h;
            BANNER_H_MOBILE = hm;

            // --- Transition speed ---
            var speedMap = { none: 0, fast: 150, normal: 300, slow: 600 };
            TRANSITION_MS = speedMap.hasOwnProperty(CONFIG.transitionSpeed) ? speedMap[CONFIG.transitionSpeed] : 300;
            var dur = (TRANSITION_MS / 1000).toFixed(2) + "s";
            root.style.setProperty("--jf-tr", "opacity " + dur + " ease,transform " + dur + " ease");
            root.style.setProperty("--jf-dur", dur);

            // --- Font size ---
            var fs = Math.max(10, Math.min(32, CONFIG.fontSize || 14));
            root.style.setProperty("--jf-fs", fs + "px");
            root.style.setProperty("--jf-fs-m", Math.max(fs - 1, 10) + "px");

            // --- Font weight ---
            banner.style.fontWeight = CONFIG.fontBold !== false ? "bold" : "normal";

            // --- Text alignment ---
            if (CONFIG.textAlign === "left") {
                banner.style.justifyContent = "flex-start";
                banner.style.textAlign = "left";
                banner.style.paddingLeft = "16px";
                banner.style.paddingRight = "80px";
            }

            // --- Persist dismissed ---
            if (CONFIG.persistDismiss) {
                getPersistedDismissed().forEach(function (t) { dismissedMessages.add(t); });
            }
            if (CONFIG.permanentDismissible && dismissedMessages.has(PERM_DISMISS_KEY)) {
                permanentDismissed = true;
            }

            // Apply control visibility
            if (CONFIG.showDismissButton === false) closeBtn.style.display = "none";
            if (CONFIG.dismissButtonSize) closeBtn.style.fontSize = CONFIG.dismissButtonSize + "px";
            if (CONFIG.showDismissAll === false) dismissAllBtn.style.display = "none";
            if (CONFIG.dismissAllSize) dismissAllBtn.style.fontSize = CONFIG.dismissAllSize + "px";
            dismissAllBtn.textContent = CONFIG.dismissAllText || "hide all";
            // Insert banner now: SPA has finished mounting so the div won't be evicted.
            if (!banner.isConnected) { document.body.prepend(banner); }

            // Re-evaluate on every SPA navigation.
            // Jellyfin uses hash-based routing for most transitions but also calls
            // pushState/replaceState directly for some navigations (e.g. home→admin).
            // All three sources are needed. The debounce collapses any burst of
            // concurrent events into a single evaluation, preventing flash cycles.
            var navTimer = null;
            function onNavigate() {
                clearTimeout(navTimer);
                // Prevent a stale tick() from firing during the debounce window.
                if (CONFIG && (CONFIG.showInDashboard === false || anyRoutesConfigured())) clearTimeout(rotationTimer);
                navTimer = setTimeout(function () {
                    // Re-apply padding to the newly mounted .page after SPA navigation.
                    if (document.body.classList.contains('jf-banner-active')) {
                        requestAnimationFrame(applyBodyMargin);
                    }
                    // Re-check maintenance state on each navigation (unauthenticated).
                    fetch("/MaintenanceDeluxe/maintenance")
                        .then(function (r) { return r.ok ? r.json() : null; })
                        .then(function (m) { if (!m) return; MAINTENANCE = m; applyMaintenanceState(); })
                        .catch(function () {});
                    // Poll config for changes: if lastModified has advanced, reload
                    // the full config so new messages appear within one rotation cycle.
                    var tok = getToken();
                    if (tok) {
                        fetch("/MaintenanceDeluxe/config", {
                            headers: { "Authorization": "MediaBrowser Token=\"" + tok + "\"" }
                        })
                            .then(function (r) { return r.ok ? r.json() : null; })
                            .then(function (fresh) {
                                if (!fresh) return;
                                if ((fresh.lastModified || 0) !== CONFIG_LAST_MODIFIED) {
                                    CONFIG = fresh;
                                    CONFIG_LAST_MODIFIED = fresh.lastModified || 0;
                                    shuffledQueue = []; // invalidate stale queue
                                }
                                if (CONFIG.showInDashboard === false || anyRoutesConfigured()) {
                                    clearTimeout(rotationTimer);
                                    if (CONFIG.showInDashboard === false && isAdminPage()) { hideBanner(); } else { tick(); }
                                }
                            })
                            .catch(function () {
                                // Network error — fall back to existing config
                                if (CONFIG.showInDashboard === false || anyRoutesConfigured()) {
                                    clearTimeout(rotationTimer);
                                    if (CONFIG.showInDashboard === false && isAdminPage()) { hideBanner(); } else { tick(); }
                                }
                            });
                    } else if (CONFIG.showInDashboard === false || anyRoutesConfigured()) {
                        clearTimeout(rotationTimer);
                        if (CONFIG.showInDashboard === false && isAdminPage()) { hideBanner(); } else { tick(); }
                    }
                }, NAV_DEBOUNCE);
            }
            window.addEventListener("hashchange", onNavigate);
            window.addEventListener("popstate", onNavigate);
            (function () {
                function wrap(method) {
                    var orig = history[method];
                    history[method] = function () {
                        orig.apply(this, arguments);
                        onNavigate();
                    };
                }
                wrap('pushState');
                wrap('replaceState');
            }());
            // Sync dismiss state from other tabs.
            if (bc) {
                bc.onmessage = function (e) {
                    var msg = e.data;
                    if (!msg) return;
                    if (msg.type === "dismiss" && msg.text) {
                        dismissedMessages.add(msg.text);
                    } else if (msg.type === "dismissPermanent") {
                        permanentDismissed = true;
                    } else if (msg.type === "dismissAll") {
                        dismissAll = true;
                    }
                    tick();
                };
            }

            applyMaintenanceState();
            tick();
        })
        .catch(function (err) {
            console.warn("[MaintenanceDeluxe] init failed:", err);
        });
        });
})();
