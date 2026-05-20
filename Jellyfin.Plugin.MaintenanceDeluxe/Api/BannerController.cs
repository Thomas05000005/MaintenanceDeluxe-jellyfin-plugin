using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MaintenanceDeluxe.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MaintenanceDeluxe.Api;

/// <summary>
/// REST API for the MaintenanceDeluxe plugin.
/// <list type="bullet">
///   <item><c>GET /MaintenanceDeluxe/maintenance</c> — public (UUID-stripped snapshot for the login-page overlay)</item>
///   <item><c>GET /MaintenanceDeluxe/banner.js</c> — public (script injected on every page; same bytes as the JavaScriptInjector copy)</item>
///   <item><c>GET /MaintenanceDeluxe/admin.js</c> — public (admin config-page script; same security model as banner.js — UI only, all admin actions gated by RequiresElevation on their endpoints)</item>
///   <item><c>GET /MaintenanceDeluxe/admin.css</c> — public (admin config-page stylesheet; same security model as admin.js)</item>
///   <item><c>GET /MaintenanceDeluxe/preview.html</c> — public (HTML shell consumed by the admin live-preview iframe; iframe navigation does not carry Authorization headers)</item>
///   <item><c>GET /MaintenanceDeluxe/config</c> — <c>[Authorize]</c> (banner-client view; <see cref="BannerClientConfig"/>, no admin-only data)</item>
///   <item><c>GET /MaintenanceDeluxe/config-admin</c> — <c>[Authorize(Policy="RequiresElevation")]</c> (full <see cref="PluginConfiguration"/> incl. webhook URL and user UUID lists)</item>
///   <item><c>POST /MaintenanceDeluxe/config</c> — <c>[Authorize(Policy="RequiresElevation")]</c> (admin-only write)</item>
///   <item><c>POST /MaintenanceDeluxe/maintenance</c> — <c>[Authorize(Policy="RequiresElevation")]</c> (admin-only toggle)</item>
///   <item><c>POST /MaintenanceDeluxe/maintenance/test-webhook</c> — <c>[Authorize(Policy="RequiresElevation")]</c> (admin-only webhook check)</item>
///   <item><c>GET /MaintenanceDeluxe/users-summary</c> — <c>[Authorize(Policy="RequiresElevation")]</c> (admin-only users list for the whitelist widget)</item>
///   <item><c>GET /MaintenanceDeluxe/active-sessions</c> — <c>[Authorize(Policy="RequiresElevation")]</c> (admin-only pre-flight check before activation: who is currently streaming)</item>
///   <item><c>GET /MaintenanceDeluxe/announcements/active</c> — <c>[Authorize]</c> (announcements not yet seen by the current user)</item>
///   <item><c>POST /MaintenanceDeluxe/announcements/{id}/seen</c> — <c>[Authorize]</c> (mark announcement seen by current user)</item>
///   <item><c>GET /MaintenanceDeluxe/announcements/admin</c> — <c>[Authorize(Policy="RequiresElevation")]</c> (full list + seen-counts for admin UI)</item>
///   <item><c>POST /MaintenanceDeluxe/announcements/admin</c> — <c>[Authorize(Policy="RequiresElevation")]</c> (save announcements list + multi-mode)</item>
///   <item><c>POST /MaintenanceDeluxe/announcements/admin/{id}/reset-seen</c> — <c>[Authorize(Policy="RequiresElevation")]</c> (reset who-has-seen tracking)</item>
/// </list>
/// </summary>
[ApiController]
[Route("MaintenanceDeluxe")]
public class BannerController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BannerController> _logger;

    // Global rate-limit for /test-webhook (1 call / 5s, all admins combined).
    // Previously a per-IP ConcurrentDictionary, but (a) Jellyfin is typically behind a
    // reverse proxy so RemoteIpAddress is the proxy IP — all admins shared the same
    // bucket anyway — and (b) the dict grew unboundedly. /test-webhook is admin-only
    // and rarely exercised, so a single global cooldown is the simplest correct design.
    private static long _testWebhookLastCallTicks; // DateTime.UtcNow.Ticks
    private static readonly TimeSpan _testWebhookCooldown = TimeSpan.FromSeconds(5);

    /// <summary>Initializes a new instance of <see cref="BannerController"/>.</summary>
    public BannerController(
        IUserManager userManager,
        ISessionManager sessionManager,
        IHttpClientFactory httpFactory,
        ILogger<BannerController> logger)
    {
        _userManager = userManager;
        _sessionManager = sessionManager;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>Returns the banner-client view of the plugin configuration. This is the
    /// non-elevated endpoint — any authenticated user can call it, so the response is
    /// scrubbed of admin-only data (webhook URL, user UUID lists) via <see cref="BannerClientConfig"/>.
    /// banner.js never reads <c>maintenanceMode</c> from <c>/config</c> (it polls the
    /// <c>/maintenance</c> snapshot instead), so this view is sufficient.
    /// Returns 503 if the plugin instance is not yet initialised (avoids serving phantom defaults).</summary>
    [HttpGet("config")]
    [Authorize]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<BannerClientConfig> GetConfig()
    {
        if (Plugin.Instance is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Plugin not initialised yet.");
        return Ok(BannerClientConfig.From(Plugin.Instance.Configuration));
    }

    /// <summary>Returns the FULL plugin configuration including <c>maintenanceMode</c>
    /// (webhook URL, whitelistedUserIds, maintenanceDisabledUserIds, preDisabledUserIds).
    /// Admin-only — the admin config page reads from here so it can render the whitelist
    /// widget and the webhook tab. Returns 503 if the plugin instance is not yet initialised.</summary>
    [HttpGet("config-admin")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<PluginConfiguration> GetConfigAdmin()
    {
        if (Plugin.Instance is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Plugin not initialised yet.");
        return Ok(Plugin.Instance.Configuration);
    }

    /// <summary>Serves the banner client script.
    /// Public (no [Authorize]) so the preview iframe in the admin config page can load it
    /// (browsers do not send Authorization headers on iframe-initiated script requests).
    /// This is safe: the same bytes are already served publicly via JavaScriptInjector.
    /// </summary>
    [HttpGet("banner.js")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetBannerScript() =>
        ServeEmbeddedAsset("Jellyfin.Plugin.MaintenanceDeluxe.Resources.banner.js", "application/javascript");

    /// <summary>Serves the admin config-page client script. Public (no [Authorize]) for the
    /// same reason as banner.js: a &lt;script src&gt; tag in configPage.html cannot send the
    /// Authorization header, and admin-only operations are still gated server-side via
    /// the [Authorize(Policy="RequiresElevation")] attributes on the actual config endpoints.
    /// The admin.js code itself contains no secrets — it just renders the UI and POSTs to
    /// authenticated endpoints.</summary>
    [HttpGet("admin.js")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetAdminScript() =>
        ServeEmbeddedAsset("Jellyfin.Plugin.MaintenanceDeluxe.Configuration.admin.js", "application/javascript");

    /// <summary>Serves the admin config-page stylesheet. Same public/security model as
    /// admin.js — the stylesheet contains no secrets and is loaded via <c>&lt;link&gt;</c>
    /// from configPage.html.</summary>
    [HttpGet("admin.css")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetAdminStylesheet() =>
        ServeEmbeddedAsset("Jellyfin.Plugin.MaintenanceDeluxe.Configuration.admin.css", "text/css");

    // Whitelist of font file names allowed by the fonts endpoint. Maps the URL slug
    // to the embedded resource basename. Keeps path traversal impossible: the user
    // input is matched against this dictionary, never concatenated into a path.
    private static readonly Dictionary<string, string> _fontResources = new(StringComparer.Ordinal)
    {
        ["inter"] = "Inter-Variable.woff2",
        ["jetbrains-mono"] = "JetBrainsMono-Variable.woff2",
        ["space-grotesk"] = "SpaceGrotesk-Variable.woff2",
        ["manrope"] = "Manrope-Variable.woff2"
    };

    /// <summary>Serves the embedded webfont matching <paramref name="slug"/>.
    /// Public (no [Authorize]) so banner.js can @font-face them on the login page before auth.
    /// Long-cached (1 year) — fonts are versioned by URL via the assembly-version query string
    /// banner.js appends, so a plugin upgrade naturally invalidates the cache through a new URL.
    /// </summary>
    [HttpGet("fonts/{slug}.woff2")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetFont(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || !_fontResources.TryGetValue(slug, out var file))
            return NotFound();

        var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Jellyfin.Plugin.MaintenanceDeluxe.Resources.Fonts." + file);
        if (stream is null) return NotFound();

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        return File(stream, "font/woff2");
    }

    // ETag tied to the assembly version — changes on every plugin upgrade so the browser
    // refetches fresh assets instead of serving stale ones from a previous version. Cached
    // once at process start; the version doesn't change at runtime.
    private static readonly string _assetEtagVersion = "\"v"
        + (typeof(BannerController).Assembly.GetName().Version?.ToString() ?? "unknown") + "\"";

    /// <summary>Shared helper for the public asset endpoints (banner.js, admin.js, admin.css).
    /// Uses ETag-based revalidation tied to the plugin assembly version: the browser caches
    /// the asset but must revalidate on every request (cheap conditional GET). If the version
    /// hasn't changed, we return 304 Not Modified with no body. When the plugin is upgraded,
    /// the version bumps, the ETag changes, and the browser fetches the fresh asset. This
    /// fixes the "stale admin UI after plugin upgrade" class of bug that v0.3.4-v0.3.10
    /// suffered with the plain max-age=300 strategy. nosniff guards MIME-confusion regardless.
    /// 404 if the embedded resource is missing (typically a build issue).</summary>
    private IActionResult ServeEmbeddedAsset(string resourceName, string contentType)
    {
        // 304 Not Modified shortcut: if the client already has the current version, skip the
        // body entirely. Big win on every admin page load after the first one for this version.
        if (Request.Headers.TryGetValue("If-None-Match", out var inm)
            && inm.ToString() == _assetEtagVersion)
        {
            Response.Headers["ETag"] = _assetEtagVersion;
            Response.Headers["Cache-Control"] = "public, no-cache, must-revalidate";
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null)
            return NotFound();

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["ETag"] = _assetEtagVersion;
        // no-cache here means "browser MUST revalidate with ETag before reusing" (not "don't
        // cache" — that would be no-store). The 304 path above kicks in when the version
        // matches; otherwise the full body is served.
        Response.Headers["Cache-Control"] = "public, no-cache, must-revalidate";
        return File(stream, contentType);
    }

    /// <summary>Serves a minimal admin preview shell used by the config page's live-preview iframe.
    /// Public (no [Authorize]) because browsers embed iframes with cookies only, whereas Jellyfin API
    /// auth requires an Authorization header the browser does not send on iframe navigation. The shell
    /// itself contains no secrets - it just loads banner.js which reads the already-public maintenance
    /// endpoint.</summary>
    [HttpGet("preview.html")]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ContentResult GetPreviewShell()
    {
        const string html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<title>MaintenanceDeluxe preview</title>
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<style>html,body{margin:0;padding:0;height:100%;background:#000;overflow:hidden;}</style>
</head>
<body>
<script src=""/MaintenanceDeluxe/banner.js""></script>
</body>
</html>";
        // SAMEORIGIN: this shell is consumed by the admin live-preview iframe within
        // Jellyfin itself. Block any third-party site from embedding it (clickjacking).
        // nosniff: guard against MIME-confusion if a downstream proxy strips the type.
        Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return new ContentResult
        {
            Content = html,
            ContentType = "text/html; charset=utf-8",
            StatusCode = 200
        };
    }

    /// <summary>Saves the plugin configuration.</summary>
    [HttpPost("config")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult SaveConfig([FromBody] PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (Plugin.Instance is null)
            return NotFound();

        // Clamp numeric fields to valid ranges
        config.DisplayDuration = Math.Max(1, config.DisplayDuration);
        config.PauseDuration = Math.Max(0, config.PauseDuration);
        config.BannerHeight = Math.Clamp(config.BannerHeight, 24, 80);

        // Normalise enum-like string fields to prevent unknown tokens being stored
        string[] validTextAlign = ["center", "left"];
        if (!string.IsNullOrEmpty(config.TextAlign) && !Array.Exists(validTextAlign, v => v == config.TextAlign))
            config.TextAlign = "center";

        string[] validTransitionSpeed = ["none", "fast", "normal", "slow"];
        if (!string.IsNullOrEmpty(config.TransitionSpeed) && !Array.Exists(validTransitionSpeed, v => v == config.TransitionSpeed))
            config.TransitionSpeed = "normal";

        // Validate colours on every collection holding a Bg/Color string (presets + messages +
        // permanent entries). Invalid values are silently rebased to the type default so a
        // typo in admin doesn't bake arbitrary CSS (`"red;position:fixed"`) into the config.
        if (config.ColorPresets is not null)
        {
            foreach (var p in config.ColorPresets)
            {
                p.Bg = NormaliseHexColorOrDefault(p.Bg, "#1976d2");
                p.Color = NormaliseHexColorOrDefault(p.Color, "#ffffff");
            }
        }
        if (config.RotationMessages is not null)
        {
            foreach (var m in config.RotationMessages)
            {
                m.Bg = NormaliseHexColorOrDefault(m.Bg, "#1976d2");
                m.Color = NormaliseHexColorOrDefault(m.Color, "#ffffff");
            }
        }
        if (config.PermanentOverride?.Entries is not null)
        {
            foreach (var e in config.PermanentOverride.Entries)
            {
                e.Bg = NormaliseHexColorOrDefault(e.Bg, "#2e7d32");
                e.Color = NormaliseHexColorOrDefault(e.Color, "#ffffff");
            }
        }

        // Validate URL schemes — reject javascript: and other non-http(s) schemes
        if (config.PermanentOverride?.Entries is not null)
        {
            foreach (var entry in config.PermanentOverride.Entries)
            {
                if (!IsUrlSafe(entry.Url))
                    return BadRequest($"Invalid URL in permanent entry: only http://, https://, and relative URLs are permitted.");
            }

            // Clamp active index to valid range
            var entryCount = config.PermanentOverride.Entries.Count;
            config.PermanentOverride.ActiveIndex = entryCount == 0
                ? -1
                : Math.Clamp(config.PermanentOverride.ActiveIndex, -1, entryCount - 1);
        }

        if (config.RotationMessages is not null)
        {
            foreach (var msg in config.RotationMessages)
            {
                if (!IsUrlSafe(msg.Url))
                    return BadRequest($"Invalid URL in rotation message: only http://, https://, and relative URLs are permitted.");
            }
        }

        // Sanitize and validate schedule types
        if (config.PermanentOverride?.Entries is not null)
        {
            var err = ValidateSchedules(config.PermanentOverride.Entries.Select(e => e.Schedule), "permanent entry");
            if (err is not null) return BadRequest(err);
        }
        if (config.RotationMessages is not null)
        {
            var err = ValidateSchedules(config.RotationMessages.Select(m => m.Schedule), "rotation message");
            if (err is not null) return BadRequest(err);
        }

        // Sanitize route patterns (trim, remove empty) then validate
        config.PermanentOverride?.Entries?.ForEach(e => SanitizeRouteList(e.Routes));
        config.RotationMessages?.ForEach(m => SanitizeRouteList(m.Routes));

        if (config.PermanentOverride?.Entries is not null)
        {
            var err = ValidateRoutes(config.PermanentOverride.Entries.Select(e => (List<string>?)e.Routes), "permanent entry");
            if (err is not null) return BadRequest(err);
        }
        if (config.RotationMessages is not null)
        {
            var err = ValidateRoutes(config.RotationMessages.Select(m => (List<string>?)m.Routes), "rotation message");
            if (err is not null) return BadRequest(err);
        }

        // Maintenance mode is managed by its own endpoint — preserve the live state unchanged.
        config.MaintenanceMode = Plugin.Instance.Configuration.MaintenanceMode ?? new MaintenanceSetting();

        // v0.7.0: LastModified must be monotonic. If the system clock jumps backwards (NTP
        // correction, admin manually setting time), a naive `now` would shrink LastModified
        // and break client-side if-modified-since style polling. Math.Max ensures we never
        // regress, only stall briefly until real time catches up.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        config.LastModified = Math.Max(config.LastModified, now);
        Plugin.Instance.UpdateConfiguration(config);
        Plugin.Instance.SaveConfiguration();
        return NoContent();
    }

    /// <summary>Returns the current maintenance mode configuration.
    /// Public (no [Authorize]) so the login-page overlay works for unauthenticated users.
    /// Returns a stripped-down snapshot — UUID lists (whitelist, disabled, pre-disabled)
    /// and webhook URL are excluded so they never leak to anonymous callers.
    /// Returns 503 if the plugin instance is not yet initialised.</summary>
    [HttpGet("maintenance")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<PublicMaintenanceSnapshot> GetMaintenance()
    {
        if (Plugin.Instance is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Plugin not initialised yet.");
        var maint = Plugin.Instance.Configuration.MaintenanceMode ?? new MaintenanceSetting();
        // Anonymous endpoint — banner.js polls it on every SPA navigation. Allow a short
        // public cache so a tab that navigates 10 times in 10s only hits the server once.
        // 10s is short enough that admin toggles propagate to clients within one cycle.
        Response.Headers["Cache-Control"] = "public, max-age=10";
        return Ok(PublicMaintenanceSnapshot.From(maint));
    }

    /// <summary>Saves the maintenance mode configuration. Handles user enable/disable transitions server-side.</summary>
    [HttpPost("maintenance")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SaveMaintenance([FromBody] MaintenanceSetting maintenance)
    {
        ArgumentNullException.ThrowIfNull(maintenance);

        if (Plugin.Instance is null)
            return NotFound();

        if (!IsUrlSafe(maintenance.StatusUrl))
            return BadRequest("Invalid statusUrl: only http://, https://, and relative URLs are permitted.");

        if (maintenance.ScheduledStart.HasValue
            && maintenance.ScheduledEnd.HasValue
            && maintenance.ScheduledEnd.Value <= maintenance.ScheduledStart.Value)
            return BadRequest("scheduledEnd must be after scheduledStart.");

        // Refuse a scheduledRestart absurdly far in the future. The scheduler polls
        // every minute, so a 50-year-out value just sits and pollutes config; cap it
        // to a reasonable horizon so typos can't stay invisible forever.
        if (maintenance.ScheduledRestart.HasValue
            && maintenance.ScheduledRestart.Value > DateTime.UtcNow.AddDays(MaxScheduledRestartHorizonDays))
            return BadRequest($"scheduledRestart must be within {MaxScheduledRestartHorizonDays} days from now.");

        // Webhook URL must be https:// (no http:// — secrets in webhook tokens must not transit cleartext).
        // v0.7.0: SSRF defense — reject loopback / private / link-local / metadata hosts.
        if (maintenance.Webhook is not null && !string.IsNullOrWhiteSpace(maintenance.Webhook.Url))
        {
            var (safe, reason) = IsWebhookHostSafe(maintenance.Webhook.Url);
            if (!safe) return BadRequest(reason ?? "Webhook URL refused.");
            if (!Uri.TryCreate(maintenance.Webhook.Url, UriKind.Absolute, out var hookUri)
                || hookUri.Scheme != Uri.UriSchemeHttps)
                return BadRequest("Webhook URL must be a valid https:// URL.");

            // Soft warning when the host is not a recognised webhook provider. Doesn't
            // block the save — Generic webhooks to admin's own infra are legitimate —
            // but flags accidental SSRF-as-a-feature (e.g. typo'd internal hostname,
            // pasted attacker-controlled URL) in the server log so it isn't silent.
            if (!IsKnownWebhookHost(hookUri.Host))
                _logger.LogWarning("Webhook URL host '{Host}' is not a recognised provider (Discord/Slack). Generic webhook accepted; verify it points to your own infrastructure.", hookUri.Host);
        }

        var config = Plugin.Instance.Configuration;
        var wasActive = config.MaintenanceMode.IsActive;

        // Update non-user-tracking fields first so the helpers see the latest values when they save.
        config.MaintenanceMode.Message = maintenance.Message ?? config.MaintenanceMode.Message;
        config.MaintenanceMode.StatusUrl = maintenance.StatusUrl;
        config.MaintenanceMode.ScheduleEnabled = maintenance.ScheduleEnabled;
        config.MaintenanceMode.ScheduledStart = maintenance.ScheduledStart;
        config.MaintenanceMode.ScheduledEnd = maintenance.ScheduledEnd;
        config.MaintenanceMode.ScheduledRestart = maintenance.ScheduledRestart;

        // Whitelist: only keep well-formed GUIDs to prevent garbage drifting in.
        config.MaintenanceMode.WhitelistedUserIds = (maintenance.WhitelistedUserIds ?? new())
            .Where(id => Guid.TryParse(id, out _))
            .Distinct()
            .ToList();

        // Webhook settings — Url already validated above as https:// or empty.
        config.MaintenanceMode.Webhook = maintenance.Webhook ?? new WebhookSettings();

        // Rich overlay content — normalised (trim/truncate/whitelist) before persistence.
        config.MaintenanceMode.CustomTitle = NormaliseOptionalString(maintenance.CustomTitle, MaxTitleLength);
        config.MaintenanceMode.CustomSubtitle = NormaliseOptionalString(maintenance.CustomSubtitle, MaxSubtitleLength);
        config.MaintenanceMode.ReleaseNotes = NormaliseReleaseNotes(maintenance.ReleaseNotes);
        config.MaintenanceMode.Theme = NormaliseTheme(maintenance.Theme);
        config.MaintenanceMode.AccentColor = NormaliseHexColor(maintenance.AccentColor);
        config.MaintenanceMode.CardOpacity = Math.Clamp(maintenance.CardOpacity, 0.40, 1.00);
        config.MaintenanceMode.BgTint = NormaliseHexColor(maintenance.BgTint);
        config.MaintenanceMode.AnimationSpeed = NormaliseAnimationSpeed(maintenance.AnimationSpeed);
        config.MaintenanceMode.ParticleDensity = NormaliseParticleDensity(maintenance.ParticleDensity);
        config.MaintenanceMode.BorderStyle = NormaliseBorderStyle(maintenance.BorderStyle);
        config.MaintenanceMode.ParticleCount = maintenance.ParticleCount.HasValue
            ? Math.Clamp(maintenance.ParticleCount.Value, 0, 500)
            : (int?)null;
        config.MaintenanceMode.AnimationScale = maintenance.AnimationScale.HasValue
            ? Math.Clamp(maintenance.AnimationScale.Value, 0.0, 5.0)
            : (double?)null;

        if (!wasActive && maintenance.IsActive)
        {
            // In-memory pointer update only — the helper does a single SaveConfiguration
            // at the end of its critical section, after also writing IsActive/ActivatedAt
            // and the disabled-user lists. Doing two saves here was a benign race window
            // where a concurrent GET /config-admin could see post-controller-fields but
            // pre-helper-state.
            Plugin.Instance.UpdateConfiguration(config);
            await MaintenanceHelper.ActivateAsync(_userManager, _logger).ConfigureAwait(false);
            await WebhookNotifier.NotifyAsync(
                Plugin.Instance.Configuration.MaintenanceMode.Webhook,
                WebhookEvent.Activated,
                Plugin.Instance.Configuration.MaintenanceMode,
                _httpFactory,
                _logger).ConfigureAwait(false);
        }
        else if (wasActive && !maintenance.IsActive)
        {
            // Snapshot counts BEFORE deactivation clears the lists.
            var snapshotForNotif = ClonePublicFields(Plugin.Instance.Configuration.MaintenanceMode);
            Plugin.Instance.UpdateConfiguration(config);
            await MaintenanceHelper.DeactivateAsync(_userManager, _logger).ConfigureAwait(false);
            await WebhookNotifier.NotifyAsync(
                Plugin.Instance.Configuration.MaintenanceMode.Webhook,
                WebhookEvent.Deactivated,
                snapshotForNotif,
                _httpFactory,
                _logger).ConfigureAwait(false);
        }
        else
        {
            // No state transition → no helper involved → save here.
            Plugin.Instance.UpdateConfiguration(config);
            Plugin.Instance.SaveConfiguration();
        }

        return NoContent();
    }

    /// <summary>Sends a test payload to the supplied webhook URL. Rate-limited to 1 call / 5 s per IP.</summary>
    [HttpPost("maintenance/test-webhook")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> TestWebhook([FromBody] TestWebhookRequest body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Url))
            return BadRequest(new { error = "Webhook URL is required." });

        // v0.7.0: SSRF defense applies to test calls too.
        var (safe, reason) = IsWebhookHostSafe(body.Url);
        if (!safe) return BadRequest(new { error = reason ?? "Webhook URL refused." });
        if (!Uri.TryCreate(body.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return BadRequest(new { error = "Webhook URL must be a valid https:// URL." });

        // Atomic compare-and-swap on the global timestamp: only the first caller in a
        // 5-second window passes; subsequent callers (from any admin / any IP) get 429.
        var nowTicks = DateTime.UtcNow.Ticks;
        var lastTicks = Interlocked.Read(ref _testWebhookLastCallTicks);
        if (nowTicks - lastTicks < _testWebhookCooldown.Ticks)
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Patiente quelques secondes avant un nouveau test." });
        if (Interlocked.CompareExchange(ref _testWebhookLastCallTicks, nowTicks, lastTicks) != lastTicks)
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Patiente quelques secondes avant un nouveau test." });

        var (status, response) = await WebhookNotifier.TestAsync(body.Url, _httpFactory, _logger, ct).ConfigureAwait(false);
        return Ok(new { statusCode = status, body = response });
    }

    /// <summary>Returns a lightweight summary of all Jellyfin users for the whitelist multi-select widget.</summary>
    [HttpGet("users-summary")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetUsersSummary()
    {
        var users = _userManager.Users
            .Select(u => new
            {
                id = u.Id.ToString(),
                name = u.Username,
                isAdministrator = u.Permissions.Any(p => p.Kind == Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator && p.Value),
                isDisabled = u.Permissions.Any(p => p.Kind == Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled && p.Value)
            })
            .OrderBy(u => u.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(users);
    }

    /// <summary>Returns the list of users with an active streaming session right now —
    /// consumed by the admin UI as a pre-flight check before activating maintenance,
    /// so admins don't accidentally kick someone mid-film. A session counts as "active"
    /// when <c>NowPlayingItem</c> is non-null (i.e. actually playing media); idle web
    /// sessions are excluded so we don't false-positive on every browser tab.</summary>
    [HttpGet("active-sessions")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetActiveSessions()
    {
        // v0.7.0: snapshot the live collection before LINQ. _sessionManager.Sessions is
        // a live IEnumerable that can be mutated by other threads (session start/end), and
        // a Where/Select chain over it can observe a session disappearing mid-iteration.
        var snapshot = _sessionManager.Sessions.ToList();
        var sessions = snapshot
            .Where(s => s.NowPlayingItem is not null)
            .Select(s => new
            {
                userId = s.UserId.ToString(),
                userName = s.UserName ?? "?",
                deviceName = s.DeviceName ?? "?",
                clientName = s.Client ?? "?",
                nowPlayingTitle = s.NowPlayingItem?.Name ?? "?",
                nowPlayingType = s.NowPlayingItem?.Type.ToString() ?? "?",
                lastActivityDate = s.LastActivityDate
            })
            .OrderBy(s => s.userName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(sessions);
    }

    // ── Announcements (v0.3.9) ──────────────────────────────────────────────

    /// <summary>Returns the announcements that the CURRENT user has not yet seen and
    /// that target them (by role + UUID filters). Ordered most-recent-first.
    /// banner.js calls this after login to decide whether to pop the modal.</summary>
    [HttpGet("announcements/active")]
    [Authorize]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult GetActiveAnnouncementsForCurrentUser()
    {
        if (Plugin.Instance is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Plugin not initialised yet.");

        var (userId, isAdmin) = ResolveCurrentUser();
        if (userId is null)
            return Ok(Array.Empty<object>()); // can't identify user → safe empty response

        var config = Plugin.Instance.Configuration;
        var deliverable = AnnouncementHelper.SelectDeliverableForUser(
            config.Announcements,
            config.AnnouncementsSeen,
            userId,
            isAdmin);

        // Resolve the effective theme server-side: per-announcement override wins, otherwise
        // the global default. Saves the client a lookup and keeps the theme key on every item.
        var globalTheme = NormaliseAnnouncementTheme(config.AnnouncementTheme);
        // v0.6.0: also ship the custom theme definition on each item that uses it, so
        // banner.js can build the dynamic CSS without an extra API call.
        var customTheme = config.CustomAnnouncementTheme;

        // Project to a stripped DTO — TargetRoles / TargetUserIds are admin-side metadata
        // that the end user doesn't need to see (and shouldn't, to avoid leaking the
        // existence of other targeted users in the same announcement).
        var result = deliverable.Select(a =>
        {
            var effectiveTheme = string.IsNullOrEmpty(a.Theme) ? globalTheme : a.Theme;
            return new
            {
                id = a.Id,
                version = a.Version,
                title = a.Title,
                body = a.Body,
                icon = a.Icon,
                importance = a.Importance,
                theme = effectiveTheme,
                // Only attach customTheme when the effective theme actually needs it.
                // Saves ~200 bytes per item when the user is on velours/oled/neon/glass.
                customTheme = effectiveTheme == "custom" ? customTheme : null,
                publishedAt = a.PublishedAt,
                comparisons = a.Comparisons,
                ctaLabel = a.CtaLabel,
                ctaUrl = a.CtaUrl,
                imageUrl = a.ImageUrl,
                imageAlt = a.ImageAlt
            };
        });
        return Ok(result);
    }

    /// <summary>Marks an announcement as seen by the current user. Idempotent.</summary>
    [HttpPost("announcements/{id}/seen")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult MarkAnnouncementSeen(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest("Missing announcement id.");
        if (Plugin.Instance is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Plugin not initialised yet.");

        var (userId, _) = ResolveCurrentUser();
        if (userId is null)
            return Unauthorized();

        var config = Plugin.Instance.Configuration;
        // Guard against marking-seen on a non-existent or untargeted announcement —
        // prevents users from spamming arbitrary IDs into the tracking list.
        var match = config.Announcements.FirstOrDefault(a => a.Id == id);
        if (match is null) return NoContent(); // unknown id: silent no-op (announcement may have been deleted)

        var changed = AnnouncementHelper.MarkSeen(config.AnnouncementsSeen, id, userId);
        if (changed)
        {
            Plugin.Instance.UpdateConfiguration(config);
            Plugin.Instance.SaveConfiguration();
        }
        return NoContent();
    }

    /// <summary>Admin-only: returns the full announcements list plus per-announcement
    /// "seen by" counts so the admin UI can show "12 / 35 users have seen this".</summary>
    [HttpGet("announcements/admin")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult GetAdminAnnouncements()
    {
        if (Plugin.Instance is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Plugin not initialised yet.");

        var config = Plugin.Instance.Configuration;
        var seenLookup = config.AnnouncementsSeen.ToDictionary(e => e.AnnouncementId, e => e.UserIds.Count);

        var totalUsers = _userManager.Users.Count();
        var result = config.Announcements.Select(a => new
        {
            announcement = a,
            seenCount = seenLookup.TryGetValue(a.Id, out var c) ? c : 0,
            totalUsers
        });
        return Ok(new
        {
            multiMode = config.AnnouncementMultiMode,
            theme = config.AnnouncementTheme,
            customTheme = config.CustomAnnouncementTheme,
            items = result
        });
    }

    /// <summary>Admin-only: replaces the entire announcements list. Validates URLs,
    /// normalises enums / roles / UUIDs, assigns server-generated IDs to new entries,
    /// and prunes orphaned seen-tracking entries.</summary>
    [HttpPost("announcements/admin")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult SaveAdminAnnouncements([FromBody] SaveAnnouncementsRequest body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (Plugin.Instance is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Plugin not initialised yet.");

        var incoming = body.Announcements ?? new List<Announcement>();

        // Per-entry validation + normalisation pass.
        foreach (var a in incoming)
        {
            if (!IsUrlSafe(a.CtaUrl))
                return BadRequest($"Invalid ctaUrl on announcement '{a.Title}': only http(s) or relative URLs allowed.");
            // v0.5.3: image URL must follow the same allowlist as ctaUrl (no protocol-relative).
            if (!IsUrlSafe(a.ImageUrl))
                return BadRequest($"Invalid imageUrl on announcement '{a.Title}': only http(s) or relative URLs allowed.");

            a.Title = NormaliseOptionalString(a.Title, 200) ?? string.Empty;
            a.Version = NormaliseOptionalString(a.Version, 64) ?? string.Empty;
            a.Body = NormaliseOptionalString(a.Body, 8000) ?? string.Empty;
            a.Icon = NormaliseIcon(a.Icon);
            a.CtaLabel = NormaliseOptionalString(a.CtaLabel, 80);
            a.Importance = AnnouncementHelper.NormaliseImportance(a.Importance);
            a.TargetRoles = AnnouncementHelper.NormaliseTargetRoles(a.TargetRoles);
            a.TargetUserIds = AnnouncementHelper.NormaliseTargetUserIds(a.TargetUserIds);
            a.Theme = NormaliseAnnouncementThemeOverride(a.Theme);
            // v0.5.3: trim + cap image fields. Image URL allowlist already enforced above.
            a.ImageUrl = NormaliseOptionalString(a.ImageUrl, 2000);
            a.ImageAlt = NormaliseOptionalString(a.ImageAlt, 200);
            // Clamp ExpireAfterDays to a sensible 1..365 window. null means "never auto-expire".
            // Values <= 0 are dropped (treated as "no expiration") to avoid permanently-expired
            // entries that admins would have to manually toggle. Values > 365 are clamped down so
            // a typo (365000) doesn't produce a meaningless year-2400 expiration.
            if (a.ExpireAfterDays is int days)
            {
                if (days <= 0) a.ExpireAfterDays = null;
                else if (days > 365) a.ExpireAfterDays = 365;
            }
            // IsDraft is a plain bool — no normalisation needed beyond the [FromBody] binding.

            // Schedule: reuse the same whitelist validation as banner messages so an admin can't
            // ship an unknown type string (would silently fall through to "always" client-side).
            if (a.Schedule is not null)
            {
                var schedErr = ValidateSchedules(new[] { a.Schedule }, $"announcement '{a.Title}'");
                if (schedErr is not null) return BadRequest(schedErr);
            }

            // Cap and normalise comparison rows (admin can't ship an unbounded list).
            a.Comparisons = (a.Comparisons ?? new()).Take(20).Select(c => new AnnouncementComparison
            {
                Label = NormaliseOptionalString(c.Label, 120) ?? string.Empty,
                Before = NormaliseOptionalString(c.Before, 120) ?? string.Empty,
                After = NormaliseOptionalString(c.After, 120) ?? string.Empty,
                Highlight = NormaliseOptionalString(c.Highlight, 40) ?? string.Empty
            }).ToList();

            // Assign GUID to new entries so tracking can attach to them.
            if (string.IsNullOrWhiteSpace(a.Id)) a.Id = Guid.NewGuid().ToString();
        }

        var config = Plugin.Instance.Configuration;
        config.Announcements = incoming;
        config.AnnouncementMultiMode = AnnouncementHelper.NormaliseMultiMode(body.MultiMode);
        config.AnnouncementTheme = NormaliseAnnouncementTheme(body.Theme);
        // v0.6.0: persist (or clear) the custom theme block.
        config.CustomAnnouncementTheme = NormaliseCustomAnnouncementTheme(body.CustomTheme);
        // Drop tracking for announcements that no longer exist.
        AnnouncementHelper.PruneOrphanedSeenEntries(config.AnnouncementsSeen, incoming);

        Plugin.Instance.UpdateConfiguration(config);
        Plugin.Instance.SaveConfiguration();
        return NoContent();
    }

    /// <summary>Admin-only: clears the "seen by" tracking for one announcement so all
    /// targeted users see it again on their next login.</summary>
    [HttpPost("announcements/admin/{id}/reset-seen")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult ResetAnnouncementSeen(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest("Missing announcement id.");
        if (Plugin.Instance is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Plugin not initialised yet.");

        var config = Plugin.Instance.Configuration;
        var changed = AnnouncementHelper.ResetSeen(config.AnnouncementsSeen, id);
        if (changed)
        {
            Plugin.Instance.UpdateConfiguration(config);
            Plugin.Instance.SaveConfiguration();
        }
        return NoContent();
    }

    /// <summary>Body shape for <see cref="SaveAdminAnnouncements"/>.</summary>
    public class SaveAnnouncementsRequest
    {
        /// <summary>Gets or sets the full announcements list (replaces server-side list).</summary>
        public List<Announcement>? Announcements { get; set; }

        /// <summary>Gets or sets the multi-announcement display mode (whitelisted server-side).</summary>
        public string? MultiMode { get; set; }

        /// <summary>Gets or sets the global announcement-modal theme key (whitelisted server-side).
        /// Applies to every announcement unless an entry has its own <see cref="Announcement.Theme"/> override.</summary>
        public string? Theme { get; set; }

        /// <summary>Gets or sets the optional custom theme definition (v0.6.0). Persisted in
        /// <see cref="PluginConfiguration.CustomAnnouncementTheme"/>. Pass null to delete the
        /// custom theme entirely (the "custom" key in <see cref="Theme"/> will then fall back
        /// to velours on the client).</summary>
        public CustomAnnouncementTheme? CustomTheme { get; set; }
    }

    /// <summary>Resolves the current user from the Jellyfin auth context. Returns (null, false)
    /// if the current request isn't tied to an authenticated user (e.g. anonymous endpoint
    /// invoked anonymously). Tries the Jellyfin-specific UserId claim first, then falls back to
    /// the identity name + IUserManager lookup. Admin status comes from the user's permissions.</summary>
    private (string? UserId, bool IsAdmin) ResolveCurrentUser()
    {
        // The Jellyfin auth layer attaches the user's GUID as a claim. Try that first
        // (cheapest, no DB lookup). The claim name varies by Jellyfin version so we try
        // a couple of common ones, falling back to the identity name.
        var claimUid = User.FindFirst("Jellyfin-UserId")?.Value
                       ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(claimUid) && Guid.TryParse(claimUid, out var fromClaim))
        {
            var user = _userManager.GetUserById(fromClaim);
            if (user is not null)
            {
                var admin = user.Permissions.Any(p =>
                    p.Kind == Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator && p.Value);
                return (user.Id.ToString(), admin);
            }
        }

        // Fallback: resolve by username.
        var name = User.Identity?.Name;
        if (!string.IsNullOrEmpty(name))
        {
            var user = _userManager.GetUserByName(name);
            if (user is not null)
            {
                var admin = user.Permissions.Any(p =>
                    p.Kind == Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator && p.Value);
                return (user.Id.ToString(), admin);
            }
        }

        return (null, false);
    }

    /// <summary>Body shape for the test-webhook endpoint.</summary>
    public class TestWebhookRequest
    {
        /// <summary>Gets or sets the webhook URL to test.</summary>
        public string? Url { get; set; }
    }

    /// <summary>Snapshots the count-bearing fields of MaintenanceSetting so a deactivation
    /// notification can report accurate counts even after the lists have been cleared.
    /// v0.7.0: rendered `internal` so MaintenanceScheduleTask can reuse it instead of its
    /// own field-by-field copy that was missing ScheduledStart/ScheduledEnd/ActivatedAt.</summary>
    internal static MaintenanceSetting ClonePublicFields(MaintenanceSetting m) => new()
    {
        IsActive = m.IsActive,
        Message = m.Message,
        StatusUrl = m.StatusUrl,
        CustomTitle = m.CustomTitle,
        CustomSubtitle = m.CustomSubtitle,
        ScheduledStart = m.ScheduledStart,
        ScheduledEnd = m.ScheduledEnd,
        ScheduledRestart = m.ScheduledRestart,
        ActivatedAt = m.ActivatedAt,
        MaintenanceDisabledUserIds = new List<string>(m.MaintenanceDisabledUserIds),
        WhitelistedUserIds = new List<string>(m.WhitelistedUserIds)
    };

    private static readonly HashSet<string> _validScheduleTypes =
        new(StringComparer.Ordinal) { "always", "fixed", "annual", "weekly", "daily" };

    /// <summary>Returns an error message if any schedule in the collection has an invalid type
    /// OR an inconsistent internal configuration, or null if all are valid.
    /// v0.6.1: enforces bounds per type (fixed dates must be ordered, annual month/day in range,
    /// weekly days non-empty and in [0,6]) so admins get an immediate error instead of an
    /// announcement that silently never matches.</summary>
    internal static string? ValidateSchedules(IEnumerable<BannerSchedule?> schedules, string context)
    {
        foreach (var sch in schedules)
        {
            if (sch is null) continue;
            if (!string.IsNullOrEmpty(sch.Type) && !_validScheduleTypes.Contains(sch.Type))
                return $"Invalid schedule type \"{sch.Type}\" in {context}: must be one of always, fixed, annual, weekly, daily.";

            switch (sch.Type)
            {
                case "fixed":
                    // If both bounds are set and parseable, FixedStart must precede FixedEnd.
                    if (!string.IsNullOrEmpty(sch.FixedStart) && !string.IsNullOrEmpty(sch.FixedEnd)
                        && DateTimeOffset.TryParse(sch.FixedStart, out var fs)
                        && DateTimeOffset.TryParse(sch.FixedEnd, out var fe)
                        && fs >= fe)
                    {
                        return $"Invalid fixed schedule in {context}: fixedStart must be strictly before fixedEnd.";
                    }
                    break;

                case "annual":
                    // Bounds: 1..12 for months, 1..31 for days (allow Feb 30 etc. — calendar quirks
                    // are admin's problem, but reject 13/32+ outright since the wrap logic would misbehave).
                    if (sch.MonthStart is int ms && (ms < 1 || ms > 12))
                        return $"Invalid annual schedule in {context}: monthStart must be in 1..12 (got {ms}).";
                    if (sch.MonthEnd is int me && (me < 1 || me > 12))
                        return $"Invalid annual schedule in {context}: monthEnd must be in 1..12 (got {me}).";
                    if (sch.DayStart is int ds && (ds < 1 || ds > 31))
                        return $"Invalid annual schedule in {context}: dayStart must be in 1..31 (got {ds}).";
                    if (sch.DayEnd is int de && (de < 1 || de > 31))
                        return $"Invalid annual schedule in {context}: dayEnd must be in 1..31 (got {de}).";
                    break;

                case "weekly":
                    // v0.7.0: instead of rejecting empty weekDays (which would break legacy configs
                    // that had this case as a silent "never-active"), auto-normalise to "always".
                    // This mutation is acceptable because the caller saves the normalised list back.
                    if (sch.WeekDays is null || sch.WeekDays.Count == 0)
                    {
                        sch.Type = "always";
                        sch.WeekDays = new List<int>();
                        break;
                    }
                    foreach (var d in sch.WeekDays)
                    {
                        if (d < 0 || d > 6)
                            return $"Invalid weekly schedule in {context}: weekDays must be in 0..6 (got {d}).";
                    }
                    break;

                // "always" and "daily" have no extra structural constraints worth rejecting.
            }
        }
        return null;
    }

    /// <summary>Trims whitespace from each route pattern and removes empty entries in-place.</summary>
    private static void SanitizeRouteList(List<string> routes)
    {
        if (routes is null) return;
        routes.RemoveAll(string.IsNullOrWhiteSpace);
        for (var i = 0; i < routes.Count; i++)
            routes[i] = routes[i].Trim();
    }

    /// <summary>Returns an error message if any route pattern in the collection is invalid, or null if all are valid.
    /// Patterns are matched against window.location.hash on the client, so they accept the same character set as
    /// URL hashes plus `*` wildcards. We additionally reject `..` and consecutive `//` sequences to keep patterns
    /// shaped like real Jellyfin routes (defence in depth — no known exploit, just hygiene).</summary>
    internal static string? ValidateRoutes(IEnumerable<List<string>?> routeLists, string context)
    {
        foreach (var list in routeLists)
        {
            if (list is null) continue;
            foreach (var pattern in list)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                if (!Regex.IsMatch(pattern, @"^[A-Za-z0-9\-._/*?=&#+%]+$"))
                    return $"Invalid route pattern \"{pattern}\" in {context}.";
                if (pattern.Contains("..", StringComparison.Ordinal) || pattern.Contains("//", StringComparison.Ordinal))
                    return $"Invalid route pattern \"{pattern}\" in {context} (consecutive `..` or `//` are not allowed).";
                if (pattern.Length > 512)
                    return $"Route pattern in {context} exceeds 512 characters.";
            }
        }
        return null;
    }

    /// <summary>True if the host is a recognised webhook provider. Used only for logging,
    /// not enforcement — generic webhooks remain accepted.</summary>
    internal static bool IsKnownWebhookHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        return host.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("hooks.slack.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns true for null/empty URLs and URLs starting with http://, https://,
    /// or a single-leading-slash path. Explicitly rejects protocol-relative URLs (`//evil.com`)
    /// which would navigate to an arbitrary host when used in href attributes.</summary>
    internal static bool IsUrlSafe(string? url)
    {
        if (string.IsNullOrEmpty(url)) return true;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return true;
        // Single-leading-slash path: allow `/foo` but reject `//foo` (protocol-relative).
        return url.StartsWith("/", StringComparison.Ordinal)
            && !url.StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary>SSRF defense for outbound webhook URLs (v0.7.0). The plugin makes the request
    /// from the Jellyfin server's network context, so the admin (or an attacker via a compromised
    /// admin session) could otherwise reach internal services that the user-facing network can't.
    /// We reject:
    /// - loopback IPv4/IPv6 (127.0.0.0/8, ::1)
    /// - link-local (169.254.0.0/16, fe80::/10) — includes AWS/Azure/GCP metadata endpoints
    /// - private RFC1918 ranges (10/8, 172.16/12, 192.168/16) and IPv6 ULA (fc00::/7)
    /// - hostnames that resolve to any of the above
    /// Localhost-by-name resolution is NOT performed here (DNS round-trip would add latency
    /// and a TOCTOU window); we block the literal strings "localhost" / "localhost.localdomain"
    /// as a coarse defense. Production users that need to reach the LAN can disable webhooks
    /// or use a public reverse proxy.</summary>
    internal static (bool Safe, string? Reason) IsWebhookHostSafe(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return (false, "Empty URL.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return (false, "Malformed URL.");
        if (uri.Scheme != Uri.UriSchemeHttps) return (false, "URL must use https://.");

        var host = uri.Host;
        if (string.IsNullOrEmpty(host)) return (false, "URL has no host.");

        // Coarse hostname blocklist (no DNS lookup).
        var hostLower = host.ToLowerInvariant();
        if (hostLower == "localhost" || hostLower == "localhost.localdomain"
            || hostLower.EndsWith(".localhost", StringComparison.Ordinal)
            || hostLower.EndsWith(".internal", StringComparison.Ordinal)
            || hostLower.EndsWith(".local", StringComparison.Ordinal))
        {
            return (false, $"Webhook host '{host}' looks internal — refusing to call.");
        }

        // IP literal blocklist.
        if (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip))
                return (false, $"Webhook host '{host}' is loopback — refusing to call.");
            if (ip.IsIPv6LinkLocal)
                return (false, $"Webhook host '{host}' is IPv6 link-local — refusing to call.");
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var bytes = ip.GetAddressBytes();
                // 169.254.0.0/16 — link-local (incl. AWS/Azure/GCP metadata)
                if (bytes[0] == 169 && bytes[1] == 254)
                    return (false, $"Webhook host '{host}' is link-local (cloud metadata range) — refusing to call.");
                // 10.0.0.0/8
                if (bytes[0] == 10)
                    return (false, $"Webhook host '{host}' is private (10.0.0.0/8) — refusing to call.");
                // 172.16.0.0/12
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    return (false, $"Webhook host '{host}' is private (172.16.0.0/12) — refusing to call.");
                // 192.168.0.0/16
                if (bytes[0] == 192 && bytes[1] == 168)
                    return (false, $"Webhook host '{host}' is private (192.168.0.0/16) — refusing to call.");
                // 0.0.0.0/8 — invalid source/dest in normal traffic
                if (bytes[0] == 0)
                    return (false, $"Webhook host '{host}' uses the 0.0.0.0/8 reserved range — refusing to call.");
            }
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                var bytes = ip.GetAddressBytes();
                // fc00::/7 — Unique Local Address (IPv6 RFC1918 equivalent)
                if ((bytes[0] & 0xFE) == 0xFC)
                    return (false, $"Webhook host '{host}' is IPv6 ULA (fc00::/7) — refusing to call.");
            }
        }

        return (true, null);
    }

    // ── Rich overlay content normalisation ─────────────────────────────────────

    private const int MaxTitleLength = 200;
    private const int MaxSubtitleLength = 500;
    private const int MaxReleaseNotes = 20;
    private const int MaxReleaseNoteTitleLength = 200;
    private const int MaxReleaseNoteBodyLength = 4000;
    private const int MaxIconLength = 8;
    private const int MaxScheduledRestartHorizonDays = 30;

    private static readonly HashSet<string> _validThemes =
        new(StringComparer.Ordinal) { "velours" };

    // Announcement modal themes. Distinct from the maintenance overlay themes so each
    // surface can evolve independently — maintenance is full-page and dramatic; annonces
    // are compact and need legibility-first variants.
    private static readonly HashSet<string> _validAnnouncementThemes =
        new(StringComparer.Ordinal) { "velours", "oled", "neon", "glass", "custom" };

    /// <summary>Whitelists an announcement theme key. Fallback "velours".
    /// "custom" is always in the whitelist; the client gracefully falls back to velours
    /// if the customAnnouncementTheme block is missing/empty, so we don't have to know
    /// about it at this normalisation level.</summary>
    internal static string NormaliseAnnouncementTheme(string? value)
    {
        if (!string.IsNullOrEmpty(value) && _validAnnouncementThemes.Contains(value)) return value;
        return "velours";
    }

    /// <summary>Per-announcement theme override: null/empty means "inherit global default".
    /// Any non-empty value must be in the whitelist or it falls back to "velours".</summary>
    internal static string? NormaliseAnnouncementThemeOverride(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return _validAnnouncementThemes.Contains(trimmed) ? trimmed : null;
    }

    private static readonly HashSet<string> _validCustomThemeFonts =
        new(StringComparer.Ordinal) { "inter", "jetbrains-mono", "space-grotesk", "manrope", "system" };

    private static readonly HashSet<string> _validCustomThemeBorders =
        new(StringComparer.Ordinal) { "solid", "glow", "dashed", "none" };

    /// <summary>Validates a custom announcement theme block (v0.6.0). Invalid fields are
    /// silently nulled out so the client falls back to the velours default for that field.
    /// Returns null when the whole block is empty after normalisation (no point persisting
    /// an all-null custom theme — the admin will see "no custom configured" in the UI).</summary>
    internal static CustomAnnouncementTheme? NormaliseCustomAnnouncementTheme(CustomAnnouncementTheme? input)
    {
        if (input is null) return null;
        var label = NormaliseOptionalString(input.Label, 40);
        var accent = NormaliseHexColor(input.AccentColor);
        var backdrop = NormaliseCssColor(input.BackdropColor);
        var card = NormaliseCssColor(input.CardBackground);
        var text = NormaliseHexColor(input.TextColor);
        var font = !string.IsNullOrWhiteSpace(input.FontFamily)
            && _validCustomThemeFonts.Contains(input.FontFamily.Trim().ToLowerInvariant())
            ? input.FontFamily.Trim().ToLowerInvariant()
            : null;
        var border = !string.IsNullOrWhiteSpace(input.BorderStyle)
            && _validCustomThemeBorders.Contains(input.BorderStyle.Trim().ToLowerInvariant())
            ? input.BorderStyle.Trim().ToLowerInvariant()
            : null;
        // If nothing meaningful is set, drop the block entirely so the UI shows "not configured".
        if (label is null && accent is null && backdrop is null && card is null
            && text is null && font is null && border is null)
        {
            return null;
        }
        return new CustomAnnouncementTheme
        {
            Label = label,
            AccentColor = accent,
            BackdropColor = backdrop,
            CardBackground = card,
            TextColor = text,
            FontFamily = font,
            BorderStyle = border
        };
    }

    // v0.6.1: tighter regex with named captures + IgnoreCase. The previous version accepted
    // rgb(256,0,0) (out-of-range) and rejected "RGBA(...)" (case-sensitive). This one captures
    // each component so we can validate the bounds in C# after the regex match.
    private static readonly Regex _cssRgbaRegex = new(
        @"^rgba?\(\s*(?<r>\d{1,3})\s*,\s*(?<g>\d{1,3})\s*,\s*(?<b>\d{1,3})\s*(,\s*(?<a>0|1|0?\.\d+)\s*)?\)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Looser variant of <see cref="NormaliseHexColor"/> that also accepts
    /// rgb()/rgba() function notation, used for the custom theme backdrop / card background
    /// which need alpha transparency (the velours default is rgba(0,0,0,.68)).
    /// Validates lightly — does not allow expressions, CSS variables, or HSL.
    /// v0.6.1: enforces R/G/B in [0,255] and case-insensitive matching.</summary>
    internal static string? NormaliseCssColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (_hexColorRegex.IsMatch(trimmed)) return trimmed;
        var m = _cssRgbaRegex.Match(trimmed);
        if (!m.Success) return null;
        // Reject out-of-range RGB components — the browser would silently drop the rule
        // but the admin would have no feedback. Better to refuse at save time.
        if (!int.TryParse(m.Groups["r"].Value, out var r) || r < 0 || r > 255) return null;
        if (!int.TryParse(m.Groups["g"].Value, out var g) || g < 0 || g > 255) return null;
        if (!int.TryParse(m.Groups["b"].Value, out var b) || b < 0 || b > 255) return null;
        return trimmed;
    }

    private static readonly Regex _hexColorRegex = new(@"^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    /// <summary>Trims, truncates, and returns null for empty strings.</summary>
    internal static string? NormaliseOptionalString(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    /// <summary>Whitelists the theme key, falling back to "velours" for unknown values.</summary>
    internal static string NormaliseTheme(string? value)
    {
        if (!string.IsNullOrEmpty(value) && _validThemes.Contains(value)) return value;
        return "velours";
    }

    /// <summary>Validates hex colour format (#RRGGBB). Returns null for invalid or empty input.</summary>
    internal static string? NormaliseHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return _hexColorRegex.IsMatch(trimmed) ? trimmed : null;
    }

    /// <summary>Returns the input if it's a valid #RRGGBB hex colour, otherwise the fallback.
    /// Used for fields that MUST hold a colour (presets / banner messages) — unlike
    /// MaintenanceMode AccentColor which is nullable and means "no tint".</summary>
    internal static string NormaliseHexColorOrDefault(string? value, string fallback)
    {
        return NormaliseHexColor(value) ?? fallback;
    }

    /// <summary>Caps the release notes list length and normalises each section's fields.</summary>
    internal static List<ReleaseNoteSection> NormaliseReleaseNotes(List<ReleaseNoteSection>? notes)
    {
        if (notes is null || notes.Count == 0) return new List<ReleaseNoteSection>();

        var result = new List<ReleaseNoteSection>();
        foreach (var note in notes.Take(MaxReleaseNotes))
        {
            if (note is null) continue;
            var title = NormaliseOptionalString(note.Title, MaxReleaseNoteTitleLength);
            var body = NormaliseOptionalString(note.Body, MaxReleaseNoteBodyLength);
            // Skip empty sections silently — admin might have added and cleared a row.
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(body)) continue;
            result.Add(new ReleaseNoteSection
            {
                Icon = NormaliseIcon(note.Icon),
                Title = title ?? string.Empty,
                Body = body ?? string.Empty
            });
        }
        return result;
    }

    /// <summary>Keeps short icon strings (emoji / short identifier). Defaults to "✨" when empty.</summary>
    internal static string NormaliseIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "✨";
        var trimmed = value.Trim();
        return trimmed.Length > MaxIconLength ? trimmed[..MaxIconLength] : trimmed;
    }

    private static readonly HashSet<string> _validAnimationSpeeds =
        new(StringComparer.Ordinal) { "off", "slow", "normal", "fast" };

    /// <summary>Whitelists animation speed preset; fallback "normal".</summary>
    internal static string NormaliseAnimationSpeed(string? value)
    {
        if (!string.IsNullOrEmpty(value) && _validAnimationSpeeds.Contains(value)) return value;
        return "normal";
    }

    private static readonly HashSet<string> _validParticleDensities =
        new(StringComparer.Ordinal) { "none", "low", "normal", "dense" };

    /// <summary>Whitelists particle density preset; fallback "normal".</summary>
    internal static string NormaliseParticleDensity(string? value)
    {
        if (!string.IsNullOrEmpty(value) && _validParticleDensities.Contains(value)) return value;
        return "normal";
    }

    private static readonly HashSet<string> _validBorderStyles =
        new(StringComparer.Ordinal) { "full", "rotating", "simple", "none" };

    /// <summary>Whitelists card border style; fallback "full".</summary>
    internal static string NormaliseBorderStyle(string? value)
    {
        if (!string.IsNullOrEmpty(value) && _validBorderStyles.Contains(value)) return value;
        return "full";
    }
}
