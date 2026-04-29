using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MaintenanceDeluxe.Configuration;

/// <summary>
/// Schedule definition for a banner message or permanent entry.
/// Supported types: "always" | "fixed" | "annual" | "weekly" | "daily".
/// </summary>
public class BannerSchedule
{
    /// <summary>Gets or sets the schedule type ("always", "fixed", "annual", "weekly", "daily").</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "always";

    /// <summary>Gets or sets the fixed-range start datetime string ("YYYY-MM-DD HH:MM").</summary>
    [JsonPropertyName("fixedStart")]
    public string? FixedStart { get; set; }

    /// <summary>Gets or sets the fixed-range end datetime string ("YYYY-MM-DD HH:MM").</summary>
    [JsonPropertyName("fixedEnd")]
    public string? FixedEnd { get; set; }

    /// <summary>Gets or sets the annual range start month (1–12).</summary>
    [JsonPropertyName("monthStart")]
    public int? MonthStart { get; set; }

    /// <summary>Gets or sets the annual range start day (1–31).</summary>
    [JsonPropertyName("dayStart")]
    public int? DayStart { get; set; }

    /// <summary>Gets or sets the annual range end month (1–12).</summary>
    [JsonPropertyName("monthEnd")]
    public int? MonthEnd { get; set; }

    /// <summary>Gets or sets the annual range end day (1–31).</summary>
    [JsonPropertyName("dayEnd")]
    public int? DayEnd { get; set; }

    /// <summary>Gets or sets the days of week for weekly schedule (0 = Sun … 6 = Sat).</summary>
    [JsonPropertyName("weekDays")]
    public List<int> WeekDays { get; set; } = new();

    /// <summary>Gets or sets the time window start ("HH:MM"), used by annual, weekly, daily.</summary>
    [JsonPropertyName("timeStart")]
    public string? TimeStart { get; set; }

    /// <summary>Gets or sets the time window end ("HH:MM"), used by annual, weekly, daily.</summary>
    [JsonPropertyName("timeEnd")]
    public string? TimeEnd { get; set; }
}

/// <summary>
/// A named colour preset available in the message editors.
/// </summary>
public class ColorPreset
{
    /// <summary>Gets or sets the display label.</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the background colour.</summary>
    [JsonPropertyName("bg")]
    public string Bg { get; set; } = "#1976d2";

    /// <summary>Gets or sets the text colour.</summary>
    [JsonPropertyName("color")]
    public string Color { get; set; } = "#ffffff";
}

/// <summary>
/// A single MaintenanceDeluxe message shown in rotation.
/// </summary>
public class BannerMessage
{
    /// <summary>Gets or sets the message text.</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets the background colour (CSS value, e.g. "#1976d2").</summary>
    [JsonPropertyName("bg")]
    public string Bg { get; set; } = "#1976d2";

    /// <summary>Gets or sets the text colour (CSS value, e.g. "#ffffff").</summary>
    [JsonPropertyName("color")]
    public string Color { get; set; } = "#ffffff";

    /// <summary>Gets or sets an optional URL. When set, clicking the banner opens this URL in a new tab.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Gets or sets whether this message participates in rotation. Defaults to true.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the label of the last preset applied to this message (null if unset or preset deleted).</summary>
    [JsonPropertyName("presetLabel")]
    public string? PresetLabel { get; set; }

    /// <summary>Gets or sets the display schedule for this message (null = always show).</summary>
    [JsonPropertyName("schedule")]
    public BannerSchedule? Schedule { get; set; }

    /// <summary>Gets or sets the page-route filter. Empty = show everywhere.
    /// Non-empty = only show when the current Jellyfin hash matches at least one
    /// pattern. Supports * wildcard, matched against window.location.hash
    /// stripped of the #! prefix.</summary>
    [JsonPropertyName("routes")]
    public List<string> Routes { get; set; } = new();
}

/// <summary>
/// A single entry in the permanent banner library.
/// </summary>
public class PermanentEntry
{
    /// <summary>Gets or sets the message text.</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets the background colour.</summary>
    [JsonPropertyName("bg")]
    public string Bg { get; set; } = "#2e7d32";

    /// <summary>Gets or sets the text colour.</summary>
    [JsonPropertyName("color")]
    public string Color { get; set; } = "#ffffff";

    /// <summary>Gets or sets an optional URL. When set, clicking the banner opens this URL in a new tab.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Gets or sets the label of the last preset applied to this entry (null if unset or preset deleted).</summary>
    [JsonPropertyName("presetLabel")]
    public string? PresetLabel { get; set; }

    /// <summary>Gets or sets the display schedule for this entry (null = always show).</summary>
    [JsonPropertyName("schedule")]
    public BannerSchedule? Schedule { get; set; }

    /// <summary>Gets or sets the page-route filter. Empty = show everywhere.
    /// Non-empty = only show when the current Jellyfin hash matches at least one
    /// pattern. Supports * wildcard, matched against window.location.hash
    /// stripped of the #! prefix.</summary>
    [JsonPropertyName("routes")]
    public List<string> Routes { get; set; } = new();
}

/// <summary>
/// The permanent banner configuration: a library of entries with one active selection.
/// </summary>
public class PermanentOverride
{
    /// <summary>Gets or sets whether the permanent override banner is enabled.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>Gets or sets the index of the active entry in <see cref="Entries"/>. -1 means none selected.</summary>
    [JsonPropertyName("activeIndex")]
    public int ActiveIndex { get; set; } = -1;

    /// <summary>Gets or sets the library of permanent banner entries.</summary>
    [JsonPropertyName("entries")]
    public List<PermanentEntry> Entries { get; set; } = new();
}

/// <summary>
/// A single release note section shown in the maintenance overlay.
/// Lets admins describe what the maintenance brings to users, in rich, scannable chunks.
/// </summary>
public class ReleaseNoteSection
{
    /// <summary>Gets or sets the icon — either an emoji ("✨", "🎬", "⚡") or a short identifier.</summary>
    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "✨";

    /// <summary>Gets or sets the section title (short, one-line).</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the section body. Supports a safe subset of Markdown (bold, italic, lists).</summary>
    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;
}

/// <summary>
/// Maintenance mode configuration.
/// </summary>
public class MaintenanceSetting
{
    /// <summary>Gets or sets a value indicating whether maintenance mode is active.</summary>
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    /// <summary>Gets or sets the message displayed to users during maintenance.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "Server under maintenance. Please check back later.";

    /// <summary>Gets or sets an optional URL to a status page shown as a link in the maintenance overlay.</summary>
    [JsonPropertyName("statusUrl")]
    public string? StatusUrl { get; set; }

    /// <summary>Gets or sets the IDs of users who were already disabled before maintenance was activated.
    /// These users are NOT re-enabled when maintenance is deactivated.</summary>
    [JsonPropertyName("preDisabledUserIds")]
    public List<string> PreDisabledUserIds { get; set; } = new();

    /// <summary>Gets or sets the IDs of users that MaintenanceDeluxe disabled when maintenance was activated.
    /// These are the users that will be re-enabled when maintenance is deactivated.</summary>
    [JsonPropertyName("maintenanceDisabledUserIds")]
    public List<string> MaintenanceDisabledUserIds { get; set; } = new();

    /// <summary>Gets or sets the IDs of users excluded from maintenance disabling.
    /// These users keep their access during maintenance (e.g. admin's family, on-call staff).
    /// Edge case: a user added here AFTER activation remains disabled until the next toggle.</summary>
    [JsonPropertyName("whitelistedUserIds")]
    public List<string> WhitelistedUserIds { get; set; } = new();

    /// <summary>Gets or sets whether scheduled auto-activate/deactivate is enabled.</summary>
    [JsonPropertyName("scheduleEnabled")]
    public bool ScheduleEnabled { get; set; }

    /// <summary>Gets or sets the UTC datetime at which maintenance auto-activates. Null = no auto-activate.</summary>
    [JsonPropertyName("scheduledStart")]
    public DateTime? ScheduledStart { get; set; }

    /// <summary>Gets or sets the UTC datetime at which maintenance auto-deactivates. Null = no auto-deactivate.</summary>
    [JsonPropertyName("scheduledEnd")]
    public DateTime? ScheduledEnd { get; set; }

    /// <summary>Gets or sets the UTC datetime at which the server will be restarted. Null = no pending restart. Cleared automatically after triggering.</summary>
    [JsonPropertyName("scheduledRestart")]
    public DateTime? ScheduledRestart { get; set; }

    /// <summary>Gets or sets the UTC datetime at which maintenance was actually activated (server-managed).
    /// Used as the starting point for the progress bar when no <see cref="ScheduledStart"/> is set.
    /// Set in ActivateAsync, cleared in DeactivateAsync.</summary>
    [JsonPropertyName("activatedAt")]
    public DateTime? ActivatedAt { get; set; }

    /// <summary>Gets or sets the custom overlay title. Null or empty = use localised default.</summary>
    [JsonPropertyName("customTitle")]
    public string? CustomTitle { get; set; }

    /// <summary>Gets or sets the custom overlay subtitle. Null or empty = use localised default.</summary>
    [JsonPropertyName("customSubtitle")]
    public string? CustomSubtitle { get; set; }

    /// <summary>Gets or sets the rich release notes shown as cards in the overlay. Empty = no notes section displayed.</summary>
    [JsonPropertyName("releaseNotes")]
    public List<ReleaseNoteSection> ReleaseNotes { get; set; } = new();

    /// <summary>Gets or sets the visual theme key. Whitelisted in the controller. Defaults to "velours".</summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "velours";

    /// <summary>Gets or sets the custom hex accent colour (e.g. "#C9A96E"). Null = use theme default.</summary>
    [JsonPropertyName("accentColor")]
    public string? AccentColor { get; set; }

    /// <summary>Gets or sets the card background opacity (0.40–1.00). Default 0.72.</summary>
    [JsonPropertyName("cardOpacity")]
    public double CardOpacity { get; set; } = 0.72;

    /// <summary>Gets or sets the custom hex background tint (e.g. "#1A1412"). Null = use theme default.</summary>
    [JsonPropertyName("bgTint")]
    public string? BgTint { get; set; }

    /// <summary>Gets or sets the animation speed preset: "off" | "slow" | "normal" (default) | "fast".</summary>
    [JsonPropertyName("animationSpeed")]
    public string AnimationSpeed { get; set; } = "normal";

    /// <summary>Gets or sets the particle density preset: "none" | "low" | "normal" (default) | "dense".
    /// Used as a fallback when <see cref="ParticleCount"/> is null.</summary>
    [JsonPropertyName("particleDensity")]
    public string ParticleDensity { get; set; } = "normal";

    /// <summary>Gets or sets an explicit particle count (0..500). When non-null, overrides the
    /// <see cref="ParticleDensity"/> preset. Use this to fine-tune beyond the preset values.</summary>
    [JsonPropertyName("particleCount")]
    public int? ParticleCount { get; set; }

    /// <summary>Gets or sets an explicit animation speed multiplier (0..5.0, where 1.0 is normal,
    /// 0 disables animations, &lt;1 is faster, &gt;1 is slower). Overrides the <see cref="AnimationSpeed"/>
    /// preset when non-null.</summary>
    [JsonPropertyName("animationScale")]
    public double? AnimationScale { get; set; }

    /// <summary>Gets or sets the card border style: "full" (conic gold gradient, default) | "simple" (flat gold) | "none".</summary>
    [JsonPropertyName("borderStyle")]
    public string BorderStyle { get; set; } = "full";

    /// <summary>Gets or sets the webhook notification settings (Discord, Slack, or generic).</summary>
    [JsonPropertyName("webhook")]
    public WebhookSettings Webhook { get; set; } = new();
}

/// <summary>
/// Public-facing snapshot of maintenance state. Strips UUID lists and webhook URL so the
/// unauthenticated <c>GET /MaintenanceDeluxe/maintenance</c> endpoint cannot leak user IDs
/// or the webhook secret to the login page / banner script.
/// </summary>
public class PublicMaintenanceSnapshot
{
#pragma warning disable CS1591 // Public-facing fields mirror MaintenanceSetting; doc lives there.
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
    [JsonPropertyName("statusUrl")]
    public string? StatusUrl { get; set; }
    [JsonPropertyName("scheduleEnabled")]
    public bool ScheduleEnabled { get; set; }
    [JsonPropertyName("scheduledStart")]
    public DateTime? ScheduledStart { get; set; }
    [JsonPropertyName("scheduledEnd")]
    public DateTime? ScheduledEnd { get; set; }
    [JsonPropertyName("scheduledRestart")]
    public DateTime? ScheduledRestart { get; set; }
    [JsonPropertyName("activatedAt")]
    public DateTime? ActivatedAt { get; set; }
    [JsonPropertyName("customTitle")]
    public string? CustomTitle { get; set; }
    [JsonPropertyName("customSubtitle")]
    public string? CustomSubtitle { get; set; }
    [JsonPropertyName("releaseNotes")]
    public List<ReleaseNoteSection> ReleaseNotes { get; set; } = new();
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "velours";
    [JsonPropertyName("accentColor")]
    public string? AccentColor { get; set; }
    [JsonPropertyName("cardOpacity")]
    public double CardOpacity { get; set; } = 0.72;
    [JsonPropertyName("bgTint")]
    public string? BgTint { get; set; }
    [JsonPropertyName("animationSpeed")]
    public string AnimationSpeed { get; set; } = "normal";
    [JsonPropertyName("particleDensity")]
    public string ParticleDensity { get; set; } = "normal";
    [JsonPropertyName("particleCount")]
    public int? ParticleCount { get; set; }
    [JsonPropertyName("animationScale")]
    public double? AnimationScale { get; set; }
    [JsonPropertyName("borderStyle")]
    public string BorderStyle { get; set; } = "full";

    public static PublicMaintenanceSnapshot From(MaintenanceSetting m) => new()
    {
        IsActive = m.IsActive,
        Message = m.Message,
        StatusUrl = m.StatusUrl,
        ScheduleEnabled = m.ScheduleEnabled,
        ScheduledStart = m.ScheduledStart,
        ScheduledEnd = m.ScheduledEnd,
        ScheduledRestart = m.ScheduledRestart,
        ActivatedAt = m.ActivatedAt,
        CustomTitle = m.CustomTitle,
        CustomSubtitle = m.CustomSubtitle,
        ReleaseNotes = m.ReleaseNotes,
        Theme = m.Theme,
        AccentColor = m.AccentColor,
        CardOpacity = m.CardOpacity,
        BgTint = m.BgTint,
        AnimationSpeed = m.AnimationSpeed,
        ParticleDensity = m.ParticleDensity,
        ParticleCount = m.ParticleCount,
        AnimationScale = m.AnimationScale,
        BorderStyle = m.BorderStyle
    };
#pragma warning restore CS1591
}

/// <summary>
/// Webhook notification settings. Format (Discord / Slack / Generic) is auto-detected from the URL.
/// </summary>
public class WebhookSettings
{
    /// <summary>Gets or sets the webhook URL. Must be HTTPS. Null/empty = no webhook configured.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Gets or sets a value indicating whether webhook notifications are sent.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether to notify when maintenance activates. Default true.</summary>
    [JsonPropertyName("notifyOnActivate")]
    public bool NotifyOnActivate { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether to notify when maintenance deactivates. Default true.</summary>
    [JsonPropertyName("notifyOnDeactivate")]
    public bool NotifyOnDeactivate { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether to notify just before a scheduled server restart. Default true.</summary>
    [JsonPropertyName("notifyOnRestart")]
    public bool NotifyOnRestart { get; set; } = true;
}

/// <summary>
/// Snapshot of <see cref="PluginConfiguration"/> exposed to non-admin authenticated callers
/// via <c>GET /MaintenanceDeluxe/config</c>. Mirrors every field of PluginConfiguration EXCEPT
/// <see cref="PluginConfiguration.MaintenanceMode"/>, which carries admin-only data
/// (<c>WhitelistedUserIds</c>, <c>MaintenanceDisabledUserIds</c>, <c>PreDisabledUserIds</c>,
/// <c>Webhook.Url</c>) and is already covered for end-users by the public
/// <c>GET /MaintenanceDeluxe/maintenance</c> endpoint via <see cref="PublicMaintenanceSnapshot"/>.
/// banner.js never reads <c>config.maintenanceMode</c>, so dropping it does not affect the client.
/// </summary>
public class BannerClientConfig
{
#pragma warning disable CS1591 // Public-facing fields mirror PluginConfiguration; doc lives there.
    [JsonPropertyName("displayDuration")]
    public int DisplayDuration { get; set; }
    [JsonPropertyName("pauseDuration")]
    public int PauseDuration { get; set; }
    [JsonPropertyName("showDismissButton")]
    public bool ShowDismissButton { get; set; }
    [JsonPropertyName("dismissButtonSize")]
    public int DismissButtonSize { get; set; }
    [JsonPropertyName("showDismissAll")]
    public bool ShowDismissAll { get; set; }
    [JsonPropertyName("dismissAllSize")]
    public int DismissAllSize { get; set; }
    [JsonPropertyName("dismissAllText")]
    public string DismissAllText { get; set; } = string.Empty;
    [JsonPropertyName("permanentOverride")]
    public PermanentOverride PermanentOverride { get; set; } = new();
    [JsonPropertyName("showInDashboard")]
    public bool ShowInDashboard { get; set; }
    [JsonPropertyName("rotationEnabled")]
    public bool RotationEnabled { get; set; }
    [JsonPropertyName("rotationMessages")]
    public List<BannerMessage> RotationMessages { get; set; } = new();
    [JsonPropertyName("colorPresets")]
    public List<ColorPreset> ColorPresets { get; set; } = new();
    [JsonPropertyName("textAlign")]
    public string TextAlign { get; set; } = "center";
    [JsonPropertyName("rotationShuffle")]
    public bool RotationShuffle { get; set; }
    [JsonPropertyName("persistDismiss")]
    public bool PersistDismiss { get; set; }
    [JsonPropertyName("permanentDismissible")]
    public bool PermanentDismissible { get; set; }
    [JsonPropertyName("transitionSpeed")]
    public string TransitionSpeed { get; set; } = "normal";
    [JsonPropertyName("fontSize")]
    public int FontSize { get; set; }
    [JsonPropertyName("bannerHeight")]
    public int BannerHeight { get; set; }
    [JsonPropertyName("fontBold")]
    public bool FontBold { get; set; }
    [JsonPropertyName("showRefreshPrompt")]
    public bool ShowRefreshPrompt { get; set; }
    [JsonPropertyName("urlPopupHint")]
    public string UrlPopupHint { get; set; } = string.Empty;
    [JsonPropertyName("lastModified")]
    public long LastModified { get; set; }

    public static BannerClientConfig From(PluginConfiguration c) => new()
    {
        DisplayDuration = c.DisplayDuration,
        PauseDuration = c.PauseDuration,
        ShowDismissButton = c.ShowDismissButton,
        DismissButtonSize = c.DismissButtonSize,
        ShowDismissAll = c.ShowDismissAll,
        DismissAllSize = c.DismissAllSize,
        DismissAllText = c.DismissAllText,
        PermanentOverride = c.PermanentOverride,
        ShowInDashboard = c.ShowInDashboard,
        RotationEnabled = c.RotationEnabled,
        RotationMessages = c.RotationMessages,
        ColorPresets = c.ColorPresets,
        TextAlign = c.TextAlign,
        RotationShuffle = c.RotationShuffle,
        PersistDismiss = c.PersistDismiss,
        PermanentDismissible = c.PermanentDismissible,
        TransitionSpeed = c.TransitionSpeed,
        FontSize = c.FontSize,
        BannerHeight = c.BannerHeight,
        FontBold = c.FontBold,
        ShowRefreshPrompt = c.ShowRefreshPrompt,
        UrlPopupHint = c.UrlPopupHint,
        LastModified = c.LastModified
    };
#pragma warning restore CS1591
}

/// <summary>
/// Plugin configuration for MaintenanceDeluxe.
/// Serialized to XML by Jellyfin and exposed as JSON via <c>/MaintenanceDeluxe/config-admin</c> (admin-only).
/// Non-admin clients read a stripped-down view via <see cref="BannerClientConfig"/> on <c>/MaintenanceDeluxe/config</c>.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Initializes a new instance of the <see cref="PluginConfiguration"/> class with defaults.</summary>
    public PluginConfiguration()
    {
        DisplayDuration = 30;
        PauseDuration = 60;
        ShowDismissButton = true;
        DismissButtonSize = 20;
        ShowDismissAll = true;
        DismissAllSize = 10;
        DismissAllText = "hide all";
        ShowInDashboard = false;
        PermanentOverride = new PermanentOverride();
        RotationEnabled = true;
        RotationMessages = new List<BannerMessage>();
        // Empty list — XmlSerializer appends to non-null collections, so defaults
        // must NOT live here. The config page JS handles the first-run fallback.
        ColorPresets = new List<ColorPreset>();
        TextAlign = "center";
        RotationShuffle = true;
        PersistDismiss = false;
        PermanentDismissible = false;
        TransitionSpeed = "normal";
        FontSize = 14;
        BannerHeight = 36;
        FontBold = true;
        ShowRefreshPrompt = true;
        UrlPopupHint = string.Empty;
        MaintenanceMode = new MaintenanceSetting();
    }

    /// <summary>Gets or sets how long (seconds) each message is displayed before cycling.</summary>
    [JsonPropertyName("displayDuration")]
    public int DisplayDuration { get; set; }

    /// <summary>Gets or sets the pause (seconds) between messages. 0 = no pause.</summary>
    [JsonPropertyName("pauseDuration")]
    public int PauseDuration { get; set; }

    /// <summary>Gets or sets whether the dismiss (×) button is shown on rotation banners.</summary>
    [JsonPropertyName("showDismissButton")]
    public bool ShowDismissButton { get; set; }

    /// <summary>Gets or sets the font size (px) of the dismiss (×) button.</summary>
    [JsonPropertyName("dismissButtonSize")]
    public int DismissButtonSize { get; set; }

    /// <summary>Gets or sets whether the "hide all" button is shown on rotation banners.</summary>
    [JsonPropertyName("showDismissAll")]
    public bool ShowDismissAll { get; set; }

    /// <summary>Gets or sets the font size (px) of the "hide all" button.</summary>
    [JsonPropertyName("dismissAllSize")]
    public int DismissAllSize { get; set; }

    /// <summary>Gets or sets the label text of the "hide all" button.</summary>
    [JsonPropertyName("dismissAllText")]
    public string DismissAllText { get; set; }

    /// <summary>Gets or sets the permanent override banner (shown when Text is non-empty).</summary>
    [JsonPropertyName("permanentOverride")]
    public PermanentOverride PermanentOverride { get; set; }

    /// <summary>Gets or sets whether the banner is shown while on the admin dashboard.</summary>
    [JsonPropertyName("showInDashboard")]
    public bool ShowInDashboard { get; set; }

    /// <summary>Gets or sets whether rotation messages are enabled.</summary>
    [JsonPropertyName("rotationEnabled")]
    public bool RotationEnabled { get; set; }

    /// <summary>Gets or sets the messages shown in random rotation.</summary>
    [JsonPropertyName("rotationMessages")]
    public List<BannerMessage> RotationMessages { get; set; }

    /// <summary>Gets or sets the colour presets available in the message editors.</summary>
    [JsonPropertyName("colorPresets")]
    public List<ColorPreset> ColorPresets { get; set; }

    /// <summary>Gets or sets the banner text alignment: "center" (default) or "left".</summary>
    [JsonPropertyName("textAlign")]
    public string TextAlign { get; set; }

    /// <summary>Gets or sets whether rotation messages are shuffled (true) or shown in list order (false).</summary>
    [JsonPropertyName("rotationShuffle")]
    public bool RotationShuffle { get; set; }

    /// <summary>Gets or sets whether individually dismissed messages persist across page reloads via localStorage.</summary>
    [JsonPropertyName("persistDismiss")]
    public bool PersistDismiss { get; set; }

    /// <summary>Gets or sets whether the permanent banner can be dismissed by end-users. Default false.</summary>
    [JsonPropertyName("permanentDismissible")]
    public bool PermanentDismissible { get; set; }

    /// <summary>Gets or sets the banner fade/slide animation speed: "none" | "fast" | "normal" (default) | "slow".</summary>
    [JsonPropertyName("transitionSpeed")]
    public string TransitionSpeed { get; set; }

    /// <summary>Gets or sets the banner text font size in pixels (default 14).</summary>
    [JsonPropertyName("fontSize")]
    public int FontSize { get; set; }

    /// <summary>Gets or sets the banner height in pixels (default 36; min 24, max 80).</summary>
    [JsonPropertyName("bannerHeight")]
    public int BannerHeight { get; set; }

    /// <summary>Gets or sets whether the banner text is rendered bold (default true).</summary>
    [JsonPropertyName("fontBold")]
    public bool FontBold { get; set; }

    /// <summary>Gets or sets whether a "refresh page" prompt is shown after saving config. Default true.</summary>
    [JsonPropertyName("showRefreshPrompt")]
    public bool ShowRefreshPrompt { get; set; }

    /// <summary>Gets or sets an optional hint text shown in the URL click popup (e.g. "On mobile, copy the link instead"). Empty = no hint.</summary>
    [JsonPropertyName("urlPopupHint")]
    public string UrlPopupHint { get; set; } = string.Empty;

    /// <summary>Gets or sets the Unix timestamp (seconds) of the last config save. Used by clients to detect config changes.</summary>
    [JsonPropertyName("lastModified")]
    public long LastModified { get; set; }

    /// <summary>Gets or sets the maintenance mode configuration.</summary>
    [JsonPropertyName("maintenanceMode")]
    public MaintenanceSetting MaintenanceMode { get; set; }
}
