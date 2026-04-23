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
    // adminDismissed persists in sessionStorage so a full reload or
    // React-driven remount doesn't drop the admin's explicit decision
    // to hide the overlay for this tab session.
    function getAdminDismissed() {
        try { return sessionStorage.getItem('jf-md-admin-dismissed') === '1'; } catch (e) { return false; }
    }
    function setAdminDismissed(v) {
        try {
            if (v) sessionStorage.setItem('jf-md-admin-dismissed', '1');
            else sessionStorage.removeItem('jf-md-admin-dismissed');
        } catch (e) {}
    }
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
        } catch (_) { /* AppHost access threw \u2014 fall through to heuristic */ }
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
        // all expose window.NativeShell.openUrl \u2192 system browser). Fall through to
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
        // .page elements are position:absolute at top:0 inside a fixed container \u2014
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

    // Localisable strings. English/other locales can override this object later.
    var MD_I18N = {
        defaultTitle: "Serveur en maintenance",
        defaultSubtitle: "On peaufine le serveur. Rendez-vous juste apr\u00e8s.",
        returnSoon: "Retour imminent",
        returnAtPrefix: "Retour pr\u00e9vu \u00e0 ",
        overtime: "Finalisation en cours",
        notesTitle: "Au programme",
        statusLink: "Voir le statut d\u00e9taill\u00e9 \u2197",
        adminDismiss: "\u2715 Acc\u00e8s admin",
        loginAccess: "Acc\u00e8s administrateur \u2192",
        reconnectTitle: "Le serveur est de retour",
        reconnectSubtitle: "Redirection en cours\u2026"
    };

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

    // --- Appearance helpers (custom accent colour, anim speed, particle density, border) ---

    var HEX_RE = /^#[0-9a-fA-F]{6}$/;

    function mdHexToRgb(hex) {
        var h = hex.replace("#", "");
        return {
            r: parseInt(h.slice(0, 2), 16),
            g: parseInt(h.slice(2, 4), 16),
            b: parseInt(h.slice(4, 6), 16)
        };
    }

    function mdRgbToHex(r, g, b) {
        function c(v) {
            var n = Math.max(0, Math.min(255, Math.round(v)));
            return (n < 16 ? "0" : "") + n.toString(16);
        }
        return ("#" + c(r) + c(g) + c(b)).toUpperCase();
    }

    function mdLighten(hex, amt) {
        var c = mdHexToRgb(hex);
        return mdRgbToHex(c.r + (255 - c.r) * amt, c.g + (255 - c.g) * amt, c.b + (255 - c.b) * amt);
    }

    function mdDarken(hex, amt) {
        var c = mdHexToRgb(hex);
        return mdRgbToHex(c.r * (1 - amt), c.g * (1 - amt), c.b * (1 - amt));
    }

    function mdRgbTriplet(hex) {
        var c = mdHexToRgb(hex);
        return c.r + "," + c.g + "," + c.b;
    }

    // Derives the full palette from a single accent hex. Keeps the velours vibe
    // if accent stays gold; gracefully shifts if the admin picks a different hue.
    function mdDerivePalette(accentHex) {
        var soft = mdLighten(accentHex, 0.15);
        var bright = mdLighten(accentHex, 0.30);
        var deep = mdDarken(accentHex, 0.30);
        var shadow = mdDarken(accentHex, 0.50);
        return {
            accent: accentHex,
            accentRgb: mdRgbTriplet(accentHex),
            soft: soft,
            softRgb: mdRgbTriplet(soft),
            bright: bright,
            brightRgb: mdRgbTriplet(bright),
            deep: deep,
            deepRgb: mdRgbTriplet(deep),
            shadow: shadow,
            shadowRgb: mdRgbTriplet(shadow)
        };
    }

    var MD_ANIM_SCALE = { off: 1, slow: 2, normal: 1, fast: 0.5 };
    var MD_PARTICLE_COUNT = { none: 0, low: 10, normal: 22, dense: 40 };

    // Applies the configured appearance (colour palette, card opacity, animation scale,
    // data attributes for animation/particles/border) to the overlay element. Safe to
    // call with a partial or null-ish config - falls back to CSS defaults.
    function mdApplyAppearance(overlay, m) {
        if (!overlay) return;
        var accent = m && m.accentColor && HEX_RE.test(m.accentColor) ? m.accentColor : null;
        if (accent) {
            var p = mdDerivePalette(accent);
            overlay.style.setProperty("--md-accent", p.accent);
            overlay.style.setProperty("--md-accent-rgb", p.accentRgb);
            overlay.style.setProperty("--md-accent-soft", p.soft);
            overlay.style.setProperty("--md-accent-soft-rgb", p.softRgb);
            overlay.style.setProperty("--md-accent-bright", p.bright);
            overlay.style.setProperty("--md-accent-bright-rgb", p.brightRgb);
            overlay.style.setProperty("--md-accent-deep", p.deep);
            overlay.style.setProperty("--md-accent-deep-rgb", p.deepRgb);
            overlay.style.setProperty("--md-accent-shadow", p.shadow);
            overlay.style.setProperty("--md-accent-shadow-rgb", p.shadowRgb);
        }
        var bgTint = m && m.bgTint && HEX_RE.test(m.bgTint) ? m.bgTint : null;
        if (bgTint) {
            overlay.style.setProperty("--md-bg-tint", bgTint);
            overlay.style.setProperty("--md-bg-tint-rgb", mdRgbTriplet(bgTint));
        }
        var opacity = m && typeof m.cardOpacity === "number" ? m.cardOpacity : NaN;
        if (!isNaN(opacity) && opacity >= 0.40 && opacity <= 1.00) {
            overlay.style.setProperty("--md-card-opacity", String(opacity));
        }
        // Animation: numeric override wins over the preset. scale=0 means "off".
        var animScale;
        if (m && typeof m.animationScale === "number") {
            animScale = Math.max(0, Math.min(5, m.animationScale));
        } else {
            var anim = (m && m.animationSpeed) || "normal";
            animScale = MD_ANIM_SCALE[anim] != null ? MD_ANIM_SCALE[anim] : 1;
        }
        if (animScale === 0) {
            overlay.setAttribute("data-anim", "off");
        } else {
            overlay.setAttribute("data-anim", (m && m.animationSpeed) || "normal");
        }
        overlay.style.setProperty("--md-anim-scale", String(animScale));

        overlay.setAttribute("data-particles", (m && m.particleDensity) || "normal");
        overlay.setAttribute("data-border", (m && m.borderStyle) || "full");
    }

    function mdParticleCount(m, tier) {
        if (tier !== "full") return 0;
        if (m && typeof m.particleCount === "number") {
            return Math.max(0, Math.min(500, m.particleCount));
        }
        var density = (m && m.particleDensity) || "normal";
        return MD_PARTICLE_COUNT[density] != null ? MD_PARTICLE_COUNT[density] : 22;
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
        if (h >= 1) return "\u2248 " + h + " h " + (m > 0 ? m + " min" : "");
        return "\u2248 " + m + " min";
    }

    function parseDateOrNull(s) {
        if (!s) return null;
        var d = new Date(s);
        return isNaN(d.getTime()) ? null : d;
    }

    function cacheTimerRefs(overlay) {
        overlay._refs = {
            abs: overlay.querySelector(".jf-md-time-absolute"),
            rel: overlay.querySelector(".jf-md-time-relative"),
            box: overlay.querySelector(".jf-md-time"),
            fill: overlay.querySelector(".jf-md-progress-fill"),
            progress: overlay.querySelector(".jf-md-progress")
        };
    }

    function updateMaintenanceTimer() {
        if (!maintenanceOverlay || !MAINTENANCE) return;
        var r = maintenanceOverlay._refs;
        if (!r || !r.abs || !r.rel || !r.box || !r.fill) return;

        var start = parseDateOrNull(MAINTENANCE.scheduledStart);
        var activatedAt = parseDateOrNull(MAINTENANCE.activatedAt);
        var end = parseDateOrNull(MAINTENANCE.scheduledEnd);
        if (!end) {
            r.abs.textContent = "";
            r.rel.textContent = MD_I18N.returnSoon;
            r.fill.style.width = "100%";
            if (r.progress) r.progress.style.display = "none";
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

        r.abs.textContent = MD_I18N.returnAtPrefix + formatLocalTime(end);

        if (remaining > 0) {
            r.box.classList.remove("overtime");
            r.rel.textContent = formatRelative(remaining);
            r.fill.style.width = Math.min(100, Math.max(0, (elapsed / total) * 100)) + "%";
        } else {
            r.box.classList.add("overtime");
            var overM = Math.floor(-remaining / 60000);
            r.rel.textContent = MD_I18N.overtime + (overM > 0 ? " (+" + overM + " min)" : "");
            r.fill.style.width = "100%";
        }
    }

    function escapeMdHtml(s) {
        return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
        });
    }

    function buildMaintenanceOverlay(m, isAdmin) {
        var tier = getPerfTier();
        var title = (m && m.customTitle && m.customTitle.trim()) || MD_I18N.defaultTitle;
        var subtitle = (m && m.customSubtitle && m.customSubtitle.trim()) || MD_I18N.defaultSubtitle;
        var statusUrl = (m && m.statusUrl) || "";
        var notes = (m && m.releaseNotes) || [];

        var overlay = document.createElement("div");
        overlay.id = "jf-md-overlay";
        overlay.className = "jf-md-tier-" + tier;
        overlay.setAttribute("role", "status");
        overlay.setAttribute("aria-live", "polite");
        mdApplyAppearance(overlay, m);

        var bg = document.createElement("div");
        bg.className = "jf-md-bg";
        bg.innerHTML =
            '<div class="jf-md-beam"></div>' +
            '<div class="jf-md-blob jf-md-blob--gold"></div>' +
            '<div class="jf-md-blob jf-md-blob--midnight"></div>' +
            '<div class="jf-md-grain"></div>';
        overlay.appendChild(bg);

        var particleTotal = mdParticleCount(m, tier);
        if (particleTotal > 0) {
            var particles = document.createElement("div");
            particles.className = "jf-md-particles";
            for (var i = 0; i < particleTotal; i++) {
                var p = document.createElement("span");
                p.className = "jf-md-particle";
                // Three size classes for a natural "embers" feel.
                var sizeClass = Math.random();
                if (sizeClass < 0.6) p.classList.add("jf-md-p-sm");       // majority small
                else if (sizeClass < 0.9) p.classList.add("jf-md-p-md");  // some medium
                else p.classList.add("jf-md-p-lg");                       // rare large glowing
                p.style.left = (Math.random() * 100) + "%";
                p.style.top = (Math.random() * 100) + "%";
                // Wider duration range for more organic motion (8\u201328s).
                p.style.animationDuration = (8 + Math.random() * 20) + "s";
                p.style.animationDelay = (-Math.random() * 24) + "s";
                // Vary travel direction per particle for natural "dust/ember" feel.
                p.style.setProperty("--jf-dx", (Math.random() * 160 - 80) + "px");
                p.style.setProperty("--jf-dy", (Math.random() > 0.5 ? -1 : 1) * (70 + Math.random() * 60) + "vh");
                // Each particle gets its own flicker phase.
                p.style.setProperty("--jf-flicker-delay", (-Math.random() * 3) + "s");
                particles.appendChild(p);
            }
            overlay.appendChild(particles);
        }

        var card = document.createElement("div");
        card.className = "jf-md-card";

        var cardHtml =
            '<svg class="jf-md-logo" viewBox="0 0 64 64" xmlns="http://www.w3.org/2000/svg" aria-hidden="true"><circle cx="32" cy="32" r="27" fill="none" stroke="currentColor" stroke-width="2"/><circle cx="32" cy="32" r="5" fill="currentColor"/><circle cx="32" cy="14" r="3.5" fill="currentColor"/><circle cx="50" cy="32" r="3.5" fill="currentColor"/><circle cx="32" cy="50" r="3.5" fill="currentColor"/><circle cx="14" cy="32" r="3.5" fill="currentColor"/><circle cx="44" cy="20" r="2" fill="currentColor" opacity="0.5"/><circle cx="44" cy="44" r="2" fill="currentColor" opacity="0.5"/><circle cx="20" cy="44" r="2" fill="currentColor" opacity="0.5"/><circle cx="20" cy="20" r="2" fill="currentColor" opacity="0.5"/></svg>' +
            '<h1 class="jf-md-title">' + escapeMdHtml(title) + '</h1>' +
            '<p class="jf-md-subtitle">' + escapeMdHtml(subtitle) + '</p>';

        cardHtml +=
            '<div class="jf-md-time" aria-hidden="true">' +
                '<div class="jf-md-time-absolute"></div>' +
                '<div class="jf-md-time-relative"></div>' +
                '<div class="jf-md-progress"><div class="jf-md-progress-fill"></div></div>' +
            '</div>';

        if (notes.length > 0) {
            cardHtml += '<div class="jf-md-notes">' +
                '<h2 class="jf-md-notes-title">' + escapeMdHtml(MD_I18N.notesTitle) + '</h2>' +
                '<div class="jf-md-notes-list">';
            for (var j = 0; j < notes.length; j++) {
                var n = notes[j];
                cardHtml += '<div class="jf-md-note">' +
                    '<div class="jf-md-note-icon">' + escapeMdHtml(n.icon || "\u2728") + '</div>' +
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
            cardHtml += '<a class="jf-md-status-link" href="' + encodeURI(statusUrl) + '" target="_blank" rel="noopener noreferrer">' + escapeMdHtml(MD_I18N.statusLink) + '</a>';
        }
        // Always expose a dismiss button: admins need it to keep working, and on
        // the login page anonymous visitors need it to reach the login form.
        // Non-admin disabled users who dismiss and try to log in are rejected
        // by the backend (account disabled), so this is safe.
        var dismissLabel = isAdmin ? MD_I18N.adminDismiss : MD_I18N.loginAccess;
        cardHtml += '<button type="button" class="jf-md-dismiss">' + escapeMdHtml(dismissLabel) + '</button>';
        cardHtml += '</div>';

        card.innerHTML = cardHtml;
        overlay.appendChild(card);

        var dismissBtn = card.querySelector(".jf-md-dismiss");
        if (dismissBtn) {
            dismissBtn.addEventListener("click", function () {
                setAdminDismissed(true);
                removeMaintenanceOverlay();
            });
        }

        return overlay;
    }

    function showMaintenanceOverlay(m, isAdmin) {
        if (maintenanceOverlay) return;
        injectMaintenanceStyles();
        var overlay = buildMaintenanceOverlay(m, isAdmin);
        document.body.appendChild(overlay);
        maintenanceOverlay = overlay;
        cacheTimerRefs(overlay);
        updateMaintenanceTimer();
        requestAnimationFrame(function () { overlay.classList.add("visible"); });
        if (maintenanceTimerId) clearInterval(maintenanceTimerId);
        maintenanceTimerId = setInterval(updateMaintenanceTimer, 1000);
    }

    // Smooth reconnect: when maintenance flips off while overlay is showing, replace the card
    // content with a "back online" message then reload after a short delay.
    function showReconnectAndReload() {
        if (!maintenanceOverlay) return;
        if (maintenanceTimerId) { clearInterval(maintenanceTimerId); maintenanceTimerId = null; }
        var card = maintenanceOverlay.querySelector(".jf-md-card");
        if (card) {
            card.innerHTML =
                '<div class="jf-md-logo" style="color:#5EB35D;font-size:48px">\u2713</div>' +
                '<h1 class="jf-md-title" style="color:#5EB35D">' + escapeMdHtml(MD_I18N.reconnectTitle) + '</h1>' +
                '<p class="jf-md-subtitle">' + escapeMdHtml(MD_I18N.reconnectSubtitle) + '</p>';
        }
        setTimeout(function () {
            try { window.location.reload(); } catch (e) {}
        }, 2500);
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
            // Never block on admin-facing pages. We drop the IS_ADMIN guard here
            // because Jellyfin already redirects anonymous users away from admin
            // routes, so reaching /dashboard/* or /configurationpage implies an
            // authenticated admin session even if our async user-fetch hasn't
            // completed yet and IS_ADMIN still reads false.
            if (isAdminPage()) {
                if (maintenanceOverlay) removeMaintenanceOverlay();
                return;
            }
            if (!maintenanceOverlay && !(IS_ADMIN && getAdminDismissed())) {
                showMaintenanceOverlay(MAINTENANCE, IS_ADMIN);
            }
        } else {
            setAdminDismissed(false);
            // If the overlay is showing, the user was blocked \u2014 smooth reconnect.
            // If not (admin had dismissed, or maintenance was never shown this session), just clean up.
            if (maintenanceOverlay) {
                showReconnectAndReload();
            } else {
                removeMaintenanceOverlay();
            }
        }
    }

    // Premium overlay stylesheet \u2014 injected once on first show.
    var MD_CSS = [
        "@font-face{font-family:'Instrument Serif';font-style:normal;font-weight:400;font-display:swap;src:url(data:font/woff2;base64,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) format('woff2');}",
        "#jf-md-overlay{--md-accent:#C9A96E;--md-accent-rgb:201,169,110;",
        "--md-accent-soft:#E4C88B;--md-accent-soft-rgb:228,200,139;",
        "--md-accent-bright:#F4D89B;--md-accent-bright-rgb:244,216,155;",
        "--md-accent-deep:#8B4A3A;--md-accent-deep-rgb:139,74,58;",
        "--md-accent-shadow:#6B3A2A;--md-accent-shadow-rgb:107,58,42;",
        "--md-bg-tint:#1A1412;--md-bg-tint-rgb:26,20,18;",
        "--md-card-opacity:0.72;--md-anim-scale:1;}",
        "#jf-md-overlay{position:fixed;inset:0;z-index:" + OVERLAY_Z_INDEX + ";overflow-y:auto;overflow-x:hidden;",
        "background:var(--md-bg-tint);color:#F4EDE4;",
        "font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;",
        "display:flex;align-items:safe center;justify-content:safe center;padding:24px;",
        "opacity:0;transition:opacity .4s ease;}",
        "#jf-md-overlay.visible{opacity:1;}",
        ".jf-md-bg{position:fixed;inset:0;pointer-events:none;overflow:hidden;z-index:0;}",
        ".jf-md-blob{position:absolute;border-radius:50%;filter:blur(120px);opacity:.22;will-change:transform;}",
        ".jf-md-blob--gold{width:60vw;height:60vw;top:-20%;left:-10%;",
        "background:radial-gradient(circle,var(--md-accent) 0%,transparent 70%);",
        "animation:jf-md-drift1 calc(80s * var(--md-anim-scale,1)) ease-in-out infinite alternate;}",
        ".jf-md-blob--midnight{width:55vw;height:55vw;bottom:-25%;right:-15%;",
        "background:radial-gradient(circle,#1e2a42 0%,transparent 70%);",
        "animation:jf-md-drift2 calc(100s * var(--md-anim-scale,1)) ease-in-out infinite alternate;}",
        "@keyframes jf-md-drift1{to{transform:translate(12vw,10vh) scale(1.08);}}",
        "@keyframes jf-md-drift2{to{transform:translate(-10vw,-8vh) scale(.95);}}",
        ".jf-md-beam{position:absolute;left:50%;top:50%;width:140vmax;height:140vmax;",
        "transform:translate(-50%,-50%);pointer-events:none;",
        "background:radial-gradient(circle,rgba(var(--md-accent-rgb),.07) 0%,transparent 48%);",
        "animation:jf-md-beam-pulse calc(7s * var(--md-anim-scale,1)) ease-in-out infinite;}",
        "@keyframes jf-md-beam-pulse{0%,100%{opacity:.55;}50%{opacity:1;}}",
        ".jf-md-grain{position:absolute;inset:0;pointer-events:none;opacity:.09;mix-blend-mode:overlay;",
        "background-image:url(\"data:image/svg+xml;utf8,<svg viewBox='0 0 200 200' xmlns='http://www.w3.org/2000/svg'><filter id='n'><feTurbulence type='fractalNoise' baseFrequency='0.9' numOctaves='2' stitchTiles='stitch'/></filter><rect width='100%25' height='100%25' filter='url(%23n)'/></svg>\");}",
        ".jf-md-particles{position:fixed;inset:0;pointer-events:none;z-index:0;}",
        ".jf-md-particle{position:absolute;border-radius:50%;will-change:transform,opacity;",
        "animation:jf-md-float calc(18s * var(--md-anim-scale,1)) linear infinite,jf-md-flicker calc(2.4s * var(--md-anim-scale,1)) ease-in-out infinite;",
        "animation-delay:inherit,var(--jf-flicker-delay,0s);}",
        ".jf-md-p-sm{width:2px;height:2px;background:var(--md-accent);box-shadow:0 0 4px var(--md-accent);}",
        ".jf-md-p-md{width:3px;height:3px;background:var(--md-accent-soft);box-shadow:0 0 8px var(--md-accent-soft);}",
        ".jf-md-p-lg{width:4px;height:4px;background:var(--md-accent-bright);box-shadow:0 0 14px var(--md-accent-bright),0 0 24px rgba(var(--md-accent-bright-rgb),.4);}",
        "@keyframes jf-md-float{",
        "0%{transform:translate(0,0);}",
        "100%{transform:translate(var(--jf-dx,20px),var(--jf-dy,-100vh));}}",
        "@keyframes jf-md-flicker{",
        "0%{opacity:.15;}",
        "30%{opacity:.8;}",
        "55%{opacity:.25;}",
        "80%{opacity:.9;}",
        "100%{opacity:.15;}}",
        ".jf-md-card{position:relative;max-width:640px;width:100%;z-index:1;",
        "padding:48px 44px;border-radius:16px;background:rgba(var(--md-bg-tint-rgb),var(--md-card-opacity));",
        "border:1px solid rgba(var(--md-accent-rgb),.32);text-align:center;",
        "box-shadow:0 0 0 1px rgba(var(--md-accent-rgb),.08) inset,",
        "0 24px 70px -20px rgba(0,0,0,.55),",
        "0 0 50px -10px rgba(var(--md-accent-rgb),.22);",
        "backdrop-filter:blur(24px) saturate(1.3);",
        "-webkit-backdrop-filter:blur(24px) saturate(1.3);}",
        "@supports not ((backdrop-filter:blur(1px)) or (-webkit-backdrop-filter:blur(1px))){",
        ".jf-md-card{background:rgba(var(--md-bg-tint-rgb),.96);}}",
        ".jf-md-logo{width:56px;height:56px;margin:0 auto 22px;display:block;color:var(--md-accent-soft);",
        "filter:drop-shadow(0 0 14px rgba(var(--md-accent-rgb),.45));",
        "animation:jf-md-breathe calc(5s * var(--md-anim-scale,1)) ease-in-out infinite,jf-md-reel-spin calc(60s * var(--md-anim-scale,1)) linear infinite;}",
        "@keyframes jf-md-reel-spin{to{transform:rotate(360deg);}}",
        "@keyframes jf-md-breathe{0%,100%{opacity:.55;}50%{opacity:1;}}",
        ".jf-md-title{font-family:'Instrument Serif','Cormorant Garamond',Georgia,serif;",
        "font-weight:400;font-size:clamp(30px,5.2vw,44px);color:var(--md-accent-soft);",
        "margin:0 0 10px 0;letter-spacing:.01em;line-height:1.12;",
        "text-shadow:0 2px 20px rgba(var(--md-accent-rgb),.2);}",
        ".jf-md-subtitle{font-size:16.5px;color:#D4C3B2;margin:0 0 16px 0;line-height:1.55;}",
        ".jf-md-time{margin:32px 0 22px;padding:20px 22px;border-radius:12px;",
        "background:rgba(0,0,0,.38);border:1px solid rgba(var(--md-accent-rgb),.2);}",
        ".jf-md-time-absolute{font-family:'Geist Mono','JetBrains Mono',Menlo,Consolas,monospace;",
        "font-variant-numeric:tabular-nums;font-size:clamp(24px,4.2vw,32px);",
        "font-weight:500;color:#F4EDE4;line-height:1.2;letter-spacing:.01em;}",
        ".jf-md-time-relative{margin-top:6px;font-size:14.5px;color:var(--md-accent);font-variant-numeric:tabular-nums;font-weight:500;}",
        ".jf-md-progress{margin-top:18px;height:6px;background:rgba(var(--md-accent-rgb),.15);border-radius:3px;overflow:hidden;",
        "box-shadow:inset 0 1px 2px rgba(0,0,0,.3);}",
        ".jf-md-progress-fill{height:100%;background:linear-gradient(90deg,var(--md-accent),var(--md-accent-soft),var(--md-accent-deep));transition:width 1s linear;width:0;",
        "box-shadow:0 0 12px rgba(var(--md-accent-rgb),.5);}",
        ".jf-md-time.overtime .jf-md-progress-fill{background:#d18033;animation:jf-md-pulse 2s ease-in-out infinite;}",
        "@keyframes jf-md-pulse{50%{opacity:.45;}}",
        ".jf-md-notes{margin-top:30px;padding-top:24px;border-top:1px solid rgba(var(--md-accent-rgb),.22);}",
        ".jf-md-notes-title{font-size:11px;text-transform:uppercase;letter-spacing:.2em;",
        "color:var(--md-accent);font-weight:600;margin:0 0 18px 0;text-align:left;}",
        ".jf-md-note{display:grid;grid-template-columns:42px 1fr;gap:14px;margin-bottom:18px;text-align:left;}",
        ".jf-md-note-icon{font-size:26px;line-height:1.2;filter:drop-shadow(0 0 8px rgba(var(--md-accent-rgb),.3));}",
        ".jf-md-note-title{font-size:15px;color:var(--md-accent-soft);margin:0 0 5px 0;font-weight:500;}",
        ".jf-md-note-body{font-size:13.5px;color:#D4C3B2;line-height:1.65;}",
        ".jf-md-note-body p{margin:0 0 5px;}",
        ".jf-md-note-body strong{color:#F4EDE4;font-weight:600;}",
        ".jf-md-note-body em{font-style:italic;color:var(--md-accent-soft);}",
        ".jf-md-note-body ul{padding-left:18px;margin:6px 0;}",
        ".jf-md-note-body li{margin:3px 0;}",
        ".jf-md-footer{margin-top:26px;padding-top:20px;border-top:1px solid rgba(var(--md-accent-rgb),.18);",
        "display:flex;gap:12px;align-items:center;justify-content:center;flex-wrap:wrap;}",
        ".jf-md-status-link{color:var(--md-accent-soft);font-size:13px;text-decoration:none;padding:8px 16px;",
        "border-radius:8px;border:1px solid rgba(var(--md-accent-rgb),.4);transition:all .2s;}",
        ".jf-md-status-link:hover{background:rgba(var(--md-accent-rgb),.15);border-color:rgba(var(--md-accent-rgb),.7);}",
        ".jf-md-dismiss{padding:8px 16px;border-radius:8px;",
        "border:1px solid rgba(255,255,255,.18);background:rgba(255,255,255,.06);",
        "color:#D4C3B2;cursor:pointer;font-size:12px;font-family:inherit;transition:background .2s;}",
        ".jf-md-dismiss:hover{background:rgba(255,255,255,.12);}",
        "@media (max-width:600px){.jf-md-card{padding:32px 22px;border-radius:12px;}}",
        ".jf-md-tier-reduced .jf-md-blob,",
        ".jf-md-tier-reduced .jf-md-logo,",
        ".jf-md-tier-reduced .jf-md-time.overtime .jf-md-progress-fill{animation:none!important;}",
        ".jf-md-tier-reduced .jf-md-particles{display:none;}",
        ".jf-md-tier-reduced .jf-md-progress-fill{transition:none;}",
        ".jf-md-tier-minimal .jf-md-bg{display:none;}",
        ".jf-md-tier-minimal .jf-md-particles{display:none;}",
        ".jf-md-tier-minimal .jf-md-card{backdrop-filter:none;-webkit-backdrop-filter:none;background:rgba(36,28,24,.98);}",
        ".jf-md-tier-minimal *{animation:none!important;transition:none!important;}",
        "@supports ((mask-composite:exclude) or (-webkit-mask-composite:xor)){",
        ".jf-md-card{border-color:transparent;}",
        ".jf-md-card::before{content:\"\";position:absolute;inset:0;padding:1px;",
        "border-radius:inherit;pointer-events:none;",
        "background:conic-gradient(from 45deg,var(--md-accent-shadow),var(--md-accent),var(--md-accent-bright),var(--md-accent-soft),var(--md-accent),var(--md-accent-deep),var(--md-accent-shadow),var(--md-accent),var(--md-accent-deep));",
        "-webkit-mask:linear-gradient(#fff 0 0) content-box,linear-gradient(#fff 0 0);",
        "-webkit-mask-composite:xor;",
        "mask:linear-gradient(#fff 0 0) content-box,linear-gradient(#fff 0 0);",
        "mask-composite:exclude;}",
        "}",
        ".jf-md-tier-reduced .jf-md-beam,",
        ".jf-md-tier-minimal .jf-md-beam{animation:none;opacity:.35;}",
        /* Appearance toggles driven by data attributes on #jf-md-overlay */
        "#jf-md-overlay[data-anim=\"off\"] .jf-md-blob,",
        "#jf-md-overlay[data-anim=\"off\"] .jf-md-beam,",
        "#jf-md-overlay[data-anim=\"off\"] .jf-md-particle,",
        "#jf-md-overlay[data-anim=\"off\"] .jf-md-logo,",
        "#jf-md-overlay[data-anim=\"off\"] .jf-md-progress-fill{animation:none!important;}",
        "#jf-md-overlay[data-border=\"none\"] .jf-md-card{border-color:transparent!important;}",
        "#jf-md-overlay[data-border=\"none\"] .jf-md-card::before,",
        "#jf-md-overlay[data-border=\"simple\"] .jf-md-card::before{display:none!important;}",
        /* Rotating luminous border: animates a spinning conic gradient and
           a soft glow around the card. Uses @property for smooth angle animation. */
        "@property --md-border-angle{syntax:\"<angle>\";initial-value:0deg;inherits:false;}",
        "@keyframes jf-md-border-spin{to{--md-border-angle:360deg;}}",
        "@keyframes jf-md-border-pulse{0%,100%{opacity:.55;}50%{opacity:1;}}",
        "@supports ((mask-composite:exclude) or (-webkit-mask-composite:xor)){",
        "#jf-md-overlay[data-border=\"rotating\"] .jf-md-card::before{",
        "background:conic-gradient(from var(--md-border-angle,0deg),",
        "var(--md-accent-shadow),var(--md-accent),var(--md-accent-bright),",
        "var(--md-accent-soft),var(--md-accent),var(--md-accent-deep),",
        "var(--md-accent-shadow));",
        "animation:jf-md-border-spin calc(6s * var(--md-anim-scale,1)) linear infinite,",
        "jf-md-border-pulse calc(4s * var(--md-anim-scale,1)) ease-in-out infinite;}",
        "#jf-md-overlay[data-border=\"rotating\"] .jf-md-card{",
        "box-shadow:0 0 0 1px rgba(var(--md-accent-rgb),.08) inset,",
        "0 24px 70px -20px rgba(0,0,0,.55),",
        "0 0 60px -5px rgba(var(--md-accent-rgb),.35),",
        "0 0 120px -20px rgba(var(--md-accent-rgb),.25);}",
        "}",
        "#jf-md-overlay[data-anim=\"off\"][data-border=\"rotating\"] .jf-md-card::before{animation:none!important;}",
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

        // Currently showing a message \u2192 go to pause
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

        // In pause or first run \u2192 pick next message
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
    // React Router (HashRouter) can call history.replaceState at boot time, which
    // strips query strings from window.location. We therefore cache the flag in
    // sessionStorage the first time we see it in the URL; subsequent checks (after
    // SPA nav) read from sessionStorage.
    function isPreviewMode() {
        try {
            var href = window.location.href || "";
            if (/[?&#]md-preview=1(?:[&#]|$)/.test(href)) {
                try { sessionStorage.setItem("jf-md-preview", "1"); } catch (e) {}
                return true;
            }
            if (/[?&#]md-preview=live(?:[&#]|$)/.test(href)) return true;
            try { return sessionStorage.getItem("jf-md-preview") === "1"; } catch (e) {}
            return false;
        } catch (e) { return false; }
    }

    function isLivePreviewMode() {
        try {
            var href = window.location.href || "";
            return /[?&#]md-preview=live(?:[&#]|$)/.test(href);
        } catch (e) { return false; }
    }

    function mockMaintenance() {
        var now = new Date();
        var start = new Date(now.getTime() - 20 * 60 * 1000);  // activated 20 min ago
        var end = new Date(now.getTime() + 35 * 60 * 1000);    // ends in 35 min
        return {
            isActive: true,
            customTitle: "Serveur en maintenance",
            customSubtitle: "On en profite pour am\u00e9liorer ton exp\u00e9rience",
            message: "",
            statusUrl: "",
            activatedAt: start.toISOString(),
            scheduledStart: start.toISOString(),
            scheduledEnd: end.toISOString(),
            scheduledRestart: null,
            scheduleEnabled: true,
            releaseNotes: [
                {
                    icon: "\ud83c\udfac",
                    title: "12 nouveaux films ajout\u00e9s",
                    body: "Ajout des derni\u00e8res sorties : **Dune 3**, **Oppenheimer**, et le meilleur de A24.\n\nParfait pour une soir\u00e9e cin\u00e9ma."
                },
                {
                    icon: "\u26a1",
                    title: "Jellyfin 10.11.6 \u2192 10.12",
                    body: "- Streaming **30% plus rapide** (nouveau d\u00e9codeur)\n- Meilleure compression vid\u00e9o\n- Correction de 47 bugs"
                },
                {
                    icon: "\ud83e\uddf9",
                    title: "Nettoyage de la biblioth\u00e8que",
                    body: "Suppression des doublons + recompression des 4K trop lourds. *~80 Go r\u00e9cup\u00e9r\u00e9s.*"
                }
            ]
        };
    }

    function addPreviewBadge() {
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
            badge.textContent = "Pr\u00e9visualisation";
            overlay.appendChild(badge);
        }, 120);
    }

    function applyPreviewFallback(maint) {
        var m = maint || {};
        // Force the overlay to render in preview mode, regardless of backend state.
        m.isActive = true;
        // If no schedule configured, synthesise one so the countdown and bar have
        // something to render. We keep a 55-min window centred around now-20/now+35.
        var now = new Date();
        if (!m.scheduledStart) m.scheduledStart = new Date(now.getTime() - 20 * 60 * 1000).toISOString();
        if (!m.scheduledEnd) m.scheduledEnd = new Date(now.getTime() + 35 * 60 * 1000).toISOString();
        if (!m.activatedAt) m.activatedAt = m.scheduledStart;
        // If no release notes, fall back to demo so the preview has visible content.
        if (!m.releaseNotes || m.releaseNotes.length === 0) {
            var mock = mockMaintenance();
            m.releaseNotes = mock.releaseNotes;
        }
        return m;
    }

    if (isPreviewMode()) {
        IS_ADMIN = true;
        // Fetch the REAL saved config so admins see their own customisation in preview.
        // Only the fields not yet filled (no schedule, no release notes) fall back to
        // sensible demo data. isActive is forced true for rendering only \u2014 the server
        // state is unchanged and no user is actually blocked.
        var live = isLivePreviewMode();
        fetch("/MaintenanceDeluxe/maintenance")
            .then(function (r) { return r.ok ? r.json() : null; })
            .catch(function () { return null; })
            .then(function (maint) {
                MAINTENANCE = applyPreviewFallback(maint);
                applyMaintenanceState();
                if (!live) addPreviewBadge();
            });

        if (live) {
            // Listen for live config updates from the parent admin page. Every received
            // message re-renders the overlay with the new fields merged on top of the
            // currently-held state. We never persist anything here.
            window.addEventListener("message", function (ev) {
                var data = ev && ev.data;
                if (!data || data.type !== "md-preview-update" || !data.config) return;
                var incoming = data.config;
                var merged = {};
                for (var k1 in (MAINTENANCE || {})) merged[k1] = MAINTENANCE[k1];
                for (var k2 in incoming) merged[k2] = incoming[k2];
                MAINTENANCE = applyPreviewFallback(merged);
                removeMaintenanceOverlay();
                applyMaintenanceState();
            });
            // Announce readiness so the parent can send the initial state immediately.
            try {
                if (window.parent && window.parent !== window) {
                    window.parent.postMessage({ type: "md-preview-ready" }, "*");
                }
            } catch (e) {}
        }
        return;
    }

    // Diagnostic log (helps users/devs troubleshoot when overlay doesn't appear).
    try { console.debug("[MaintenanceDeluxe] script loaded", {
        href: window.location.href, readyState: document.readyState
    }); } catch (e) {}

    // Re-fetches /MaintenanceDeluxe/maintenance and applies state. Safe to call often.
    function refetchAndApplyMaintenance() {
        return fetch("/MaintenanceDeluxe/maintenance")
            .then(function (r) { return r.ok ? r.json() : null; })
            .catch(function () { return null; })
            .then(function (maint) {
                if (!maint) return;
                MAINTENANCE = maint;
                applyMaintenanceState();
            });
    }

    // Re-check and re-attach the overlay on SPA navigations. Essential so that
    // users arriving on the login page via client-side routing still see the
    // overlay even if our initial run happened before login.html was mounted.
    function installMaintenanceNavWatchers() {
        try { window.addEventListener("hashchange", refetchAndApplyMaintenance); } catch (e) {}
        try { window.addEventListener("popstate", refetchAndApplyMaintenance); } catch (e) {}
        try { document.addEventListener("viewshow", refetchAndApplyMaintenance); } catch (e) {}

        // Safety net: if React or any other lib removes our overlay from the
        // body, re-attach it immediately. Covers the case where Jellyfin's React
        // mounts login.html AFTER we have appended our overlay.
        if (window.MutationObserver) {
            try {
                var mo = new MutationObserver(function () {
                    if (MAINTENANCE && MAINTENANCE.isActive
                        && maintenanceOverlay
                        && !document.body.contains(maintenanceOverlay)
                        && !(IS_ADMIN && getAdminDismissed())) {
                        document.body.appendChild(maintenanceOverlay);
                    }
                });
                mo.observe(document.body, { childList: true });
            } catch (e) {}
        }
    }

    // Fetch maintenance state without auth \u2014 works even on the login page.
    fetch("/MaintenanceDeluxe/maintenance")
        .then(function (r) { return r.ok ? r.json() : null; })
        .catch(function () { return null; })
        .then(function (maint) {
            MAINTENANCE = maint;
            installMaintenanceNavWatchers();

            var token = getToken();
            if (!token) {
                // Login page / no session \u2014 show overlay if active, then stop.
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
            // pushState/replaceState directly for some navigations (e.g. home\u2192admin).
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
                                // Network error \u2014 fall back to existing config
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
