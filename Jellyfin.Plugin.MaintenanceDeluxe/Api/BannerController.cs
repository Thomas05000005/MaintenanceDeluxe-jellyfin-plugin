using System;
using System.Collections.Concurrent;
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
///   <item><c>GET /MaintenanceDeluxe/preview.html</c> — public (HTML shell consumed by the admin live-preview iframe; iframe navigation does not carry Authorization headers)</item>
///   <item><c>GET /MaintenanceDeluxe/config</c> — <c>[Authorize]</c> (full plugin config, only authenticated clients)</item>
///   <item><c>POST /MaintenanceDeluxe/config</c> — <c>[Authorize(Policy="RequiresElevation")]</c> (admin-only write)</item>
///   <item><c>POST /MaintenanceDeluxe/maintenance</c> — <c>[Authorize(Policy="RequiresElevation")]</c> (admin-only toggle)</item>
///   <item><c>POST /MaintenanceDeluxe/maintenance/test-webhook</c> — <c>[Authorize(Policy="RequiresElevation")]</c> (admin-only webhook check)</item>
///   <item><c>GET /MaintenanceDeluxe/users-summary</c> — <c>[Authorize(Policy="RequiresElevation")]</c> (admin-only users list for the whitelist widget)</item>
/// </list>
/// </summary>
[ApiController]
[Route("MaintenanceDeluxe")]
public class BannerController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BannerController> _logger;

    // Per-IP rate-limit for /test-webhook (1 call / 5s).
    private static readonly ConcurrentDictionary<string, DateTime> _testWebhookLastCall = new();
    private static readonly TimeSpan _testWebhookCooldown = TimeSpan.FromSeconds(5);

    /// <summary>Initializes a new instance of <see cref="BannerController"/>.</summary>
    public BannerController(IUserManager userManager, IHttpClientFactory httpFactory, ILogger<BannerController> logger)
    {
        _userManager = userManager;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>Returns the current plugin configuration. Returns 503 if the plugin instance is not yet initialised
    /// (avoids serving phantom defaults during a partial bootstrap).</summary>
    [HttpGet("config")]
    [Authorize]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<PluginConfiguration> GetConfig()
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
    public IActionResult GetBannerScript()
    {
        var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Jellyfin.Plugin.MaintenanceDeluxe.Resources.banner.js");
        if (stream is null)
            return NotFound();
        return File(stream, "application/javascript");
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

        config.LastModified = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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

        // Webhook URL must be https:// (no http:// — secrets in webhook tokens must not transit cleartext).
        if (maintenance.Webhook is not null && !string.IsNullOrWhiteSpace(maintenance.Webhook.Url))
        {
            if (!Uri.TryCreate(maintenance.Webhook.Url, UriKind.Absolute, out var hookUri)
                || hookUri.Scheme != Uri.UriSchemeHttps)
                return BadRequest("Webhook URL must be a valid https:// URL.");
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
            // Persist updated fields before activation so the helper reads the latest message/url.
            Plugin.Instance.UpdateConfiguration(config);
            Plugin.Instance.SaveConfiguration();
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
            Plugin.Instance.SaveConfiguration();
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

        if (!Uri.TryCreate(body.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return BadRequest(new { error = "Webhook URL must be a valid https:// URL." });

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = DateTime.UtcNow;
        if (_testWebhookLastCall.TryGetValue(ip, out var last) && now - last < _testWebhookCooldown)
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Patiente quelques secondes avant un nouveau test." });
        _testWebhookLastCall[ip] = now;

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

    /// <summary>Body shape for the test-webhook endpoint.</summary>
    public class TestWebhookRequest
    {
        /// <summary>Gets or sets the webhook URL to test.</summary>
        public string? Url { get; set; }
    }

    /// <summary>Snapshots the count-bearing fields of MaintenanceSetting so a deactivation
    /// notification can report accurate counts even after the lists have been cleared.</summary>
    private static MaintenanceSetting ClonePublicFields(MaintenanceSetting m) => new()
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

    /// <summary>Returns an error message if any schedule in the collection has an invalid type, or null if all are valid.</summary>
    private static string? ValidateSchedules(IEnumerable<BannerSchedule?> schedules, string context)
    {
        foreach (var sch in schedules)
        {
            if (sch is null) continue;
            if (!string.IsNullOrEmpty(sch.Type) && !_validScheduleTypes.Contains(sch.Type))
                return $"Invalid schedule type \"{sch.Type}\" in {context}: must be one of always, fixed, annual, weekly, daily.";
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

    /// <summary>Returns an error message if any route pattern in the collection is invalid, or null if all are valid.</summary>
    private static string? ValidateRoutes(IEnumerable<List<string>?> routeLists, string context)
    {
        foreach (var list in routeLists)
        {
            if (list is null) continue;
            foreach (var pattern in list)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                if (!Regex.IsMatch(pattern, @"^[A-Za-z0-9\-._/*?=&#+%]+$"))
                    return $"Invalid route pattern \"{pattern}\" in {context}.";
                if (pattern.Length > 512)
                    return $"Route pattern in {context} exceeds 512 characters.";
            }
        }
        return null;
    }

    /// <summary>Returns true for null/empty URLs and URLs starting with http://, https://, or /.</summary>
    private static bool IsUrlSafe(string? url)
    {
        if (string.IsNullOrEmpty(url)) return true;
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("/", StringComparison.Ordinal);
    }

    // ── Rich overlay content normalisation ─────────────────────────────────────

    private const int MaxTitleLength = 200;
    private const int MaxSubtitleLength = 500;
    private const int MaxReleaseNotes = 20;
    private const int MaxReleaseNoteTitleLength = 200;
    private const int MaxReleaseNoteBodyLength = 4000;
    private const int MaxIconLength = 8;

    private static readonly HashSet<string> _validThemes =
        new(StringComparer.Ordinal) { "velours" };

    private static readonly Regex _hexColorRegex = new(@"^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    /// <summary>Trims, truncates, and returns null for empty strings.</summary>
    private static string? NormaliseOptionalString(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    /// <summary>Whitelists the theme key, falling back to "velours" for unknown values.</summary>
    private static string NormaliseTheme(string? value)
    {
        if (!string.IsNullOrEmpty(value) && _validThemes.Contains(value)) return value;
        return "velours";
    }

    /// <summary>Validates hex colour format (#RRGGBB). Returns null for invalid or empty input.</summary>
    private static string? NormaliseHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return _hexColorRegex.IsMatch(trimmed) ? trimmed : null;
    }

    /// <summary>Caps the release notes list length and normalises each section's fields.</summary>
    private static List<ReleaseNoteSection> NormaliseReleaseNotes(List<ReleaseNoteSection>? notes)
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
    private static string NormaliseIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "✨";
        var trimmed = value.Trim();
        return trimmed.Length > MaxIconLength ? trimmed[..MaxIconLength] : trimmed;
    }

    private static readonly HashSet<string> _validAnimationSpeeds =
        new(StringComparer.Ordinal) { "off", "slow", "normal", "fast" };

    /// <summary>Whitelists animation speed preset; fallback "normal".</summary>
    private static string NormaliseAnimationSpeed(string? value)
    {
        if (!string.IsNullOrEmpty(value) && _validAnimationSpeeds.Contains(value)) return value;
        return "normal";
    }

    private static readonly HashSet<string> _validParticleDensities =
        new(StringComparer.Ordinal) { "none", "low", "normal", "dense" };

    /// <summary>Whitelists particle density preset; fallback "normal".</summary>
    private static string NormaliseParticleDensity(string? value)
    {
        if (!string.IsNullOrEmpty(value) && _validParticleDensities.Contains(value)) return value;
        return "normal";
    }

    private static readonly HashSet<string> _validBorderStyles =
        new(StringComparer.Ordinal) { "full", "rotating", "simple", "none" };

    /// <summary>Whitelists card border style; fallback "full".</summary>
    private static string NormaliseBorderStyle(string? value)
    {
        if (!string.IsNullOrEmpty(value) && _validBorderStyles.Contains(value)) return value;
        return "full";
    }
}
