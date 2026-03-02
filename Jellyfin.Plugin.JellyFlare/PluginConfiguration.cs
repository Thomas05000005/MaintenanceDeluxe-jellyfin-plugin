using System.Collections.Generic;
using System.Text.Json.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyFlare.Configuration;

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
/// A single JellyFlare message shown in rotation.
/// </summary>
public class BannerMessage
{
    /// <summary>Gets or sets the message text.</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets the background colour (CSS value, e.g. "#1976d2").</summary>
    [JsonPropertyName("bg")]
    public string Bg { get; set; } = "#1976d2";

    /// <summary>Gets or sets the text colour (CSS value, e.g. "#fff").</summary>
    [JsonPropertyName("color")]
    public string Color { get; set; } = "#fff";

    /// <summary>Gets or sets the optional display start date ("YYYY-MM-DD HH:MM").</summary>
    [JsonPropertyName("startDate")]
    public string? StartDate { get; set; }

    /// <summary>Gets or sets the optional display end date ("YYYY-MM-DD HH:MM").</summary>
    [JsonPropertyName("endDate")]
    public string? EndDate { get; set; }

    /// <summary>Gets or sets whether this message participates in rotation. Defaults to true.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the label of the last preset applied to this message (null if unset or preset deleted).</summary>
    [JsonPropertyName("presetLabel")]
    public string? PresetLabel { get; set; }
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
    public string Color { get; set; } = "#fff";

    /// <summary>Gets or sets the optional display start date.</summary>
    [JsonPropertyName("startDate")]
    public string? StartDate { get; set; }

    /// <summary>Gets or sets the optional display end date.</summary>
    [JsonPropertyName("endDate")]
    public string? EndDate { get; set; }

    /// <summary>Gets or sets the label of the last preset applied to this entry (null if unset or preset deleted).</summary>
    [JsonPropertyName("presetLabel")]
    public string? PresetLabel { get; set; }
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
/// Plugin configuration for JellyFlare.
/// Serialized to XML by Jellyfin and exposed as JSON via <c>/JellyFlare/config</c>.
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
        ShowInDashboard = true;
        PermanentOverride = new PermanentOverride();
        RotationEnabled = true;
        RotationMessages = new List<BannerMessage>();
        // Empty list — XmlSerializer appends to non-null collections, so defaults
        // must NOT live here. The config page JS handles the first-run fallback.
        ColorPresets = new List<ColorPreset>();
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
}
