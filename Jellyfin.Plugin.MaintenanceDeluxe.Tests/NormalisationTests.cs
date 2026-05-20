using System.Collections.Generic;
using Jellyfin.Plugin.MaintenanceDeluxe.Api;
using Jellyfin.Plugin.MaintenanceDeluxe.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MaintenanceDeluxe.Tests;

public class NormalisationTests
{
    [Theory]
    [InlineData("#C9A96E", "#C9A96E")]
    [InlineData("#c9a96e", "#c9a96e")]
    [InlineData("#FFFFFF", "#FFFFFF")]
    [InlineData("  #C9A96E  ", "#C9A96E")]
    [InlineData("#GGGGGG", null)]
    [InlineData("#FFF", null)]
    [InlineData("ABC123", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void NormaliseHexColor_ReturnsExpected(string? input, string? expected)
    {
        Assert.Equal(expected, BannerController.NormaliseHexColor(input));
    }

    [Theory]
    [InlineData("velours", "velours")]
    [InlineData("UNKNOWN", "velours")]
    [InlineData("", "velours")]
    [InlineData(null, "velours")]
    public void NormaliseTheme_FallsBackToVelours(string? input, string expected)
    {
        Assert.Equal(expected, BannerController.NormaliseTheme(input));
    }

    [Theory]
    [InlineData("velours", "velours")]
    [InlineData("oled", "oled")]
    [InlineData("neon", "neon")]
    [InlineData("glass", "glass")]
    [InlineData("custom", "custom")]  // v0.6.0: "custom" joins the whitelist
    [InlineData("UNKNOWN", "velours")]
    [InlineData("", "velours")]
    [InlineData(null, "velours")]
    public void NormaliseAnnouncementTheme_AcceptsFiveThemesElseVelours(string? input, string expected)
    {
        Assert.Equal(expected, BannerController.NormaliseAnnouncementTheme(input));
    }

    // ── NormaliseCustomAnnouncementTheme (v0.6.0) ──────────────────────────────

    [Fact]
    public void NormaliseCustomAnnouncementTheme_NullInput_ReturnsNull()
    {
        Assert.Null(BannerController.NormaliseCustomAnnouncementTheme(null));
    }

    [Fact]
    public void NormaliseCustomAnnouncementTheme_AllEmpty_ReturnsNull()
    {
        // Empty/whitespace fields collapse to null entries which then collapse the whole block.
        var input = new CustomAnnouncementTheme
        {
            Label = "  ",
            AccentColor = "",
            BackdropColor = null,
            CardBackground = "   ",
            TextColor = "",
            FontFamily = "",
            BorderStyle = null
        };
        Assert.Null(BannerController.NormaliseCustomAnnouncementTheme(input));
    }

    [Fact]
    public void NormaliseCustomAnnouncementTheme_PartialValidInput_KeepsValidFieldsDropsInvalidOnes()
    {
        var input = new CustomAnnouncementTheme
        {
            Label = "Mon thème",
            AccentColor = "#C9A96E",
            BackdropColor = "rgba(0,0,0,.5)",
            CardBackground = "not-a-color",   // invalid -> nulled
            TextColor = "#abcdef",
            FontFamily = "inter",
            BorderStyle = "glow"
        };
        var result = BannerController.NormaliseCustomAnnouncementTheme(input);
        Assert.NotNull(result);
        Assert.Equal("Mon thème", result!.Label);
        Assert.Equal("#C9A96E", result.AccentColor);
        Assert.Equal("rgba(0,0,0,.5)", result.BackdropColor);
        Assert.Null(result.CardBackground); // dropped (invalid)
        Assert.Equal("#abcdef", result.TextColor);
        Assert.Equal("inter", result.FontFamily);
        Assert.Equal("glow", result.BorderStyle);
    }

    [Theory]
    [InlineData("inter", "inter")]
    [InlineData("INTER", "inter")]           // case-insensitive
    [InlineData("system", "system")]
    [InlineData("jetbrains-mono", "jetbrains-mono")]
    [InlineData("Comic Sans", null)]         // unknown -> dropped
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormaliseCustomAnnouncementTheme_FontFamilyWhitelist(string? font, string? expected)
    {
        var input = new CustomAnnouncementTheme { FontFamily = font, Label = "x" };
        var result = BannerController.NormaliseCustomAnnouncementTheme(input);
        Assert.Equal(expected, result?.FontFamily);
    }

    [Theory]
    [InlineData("solid", "solid")]
    [InlineData("glow", "glow")]
    [InlineData("dashed", "dashed")]
    [InlineData("none", "none")]
    [InlineData("invisible", null)]
    [InlineData("", null)]
    public void NormaliseCustomAnnouncementTheme_BorderStyleWhitelist(string? border, string? expected)
    {
        var input = new CustomAnnouncementTheme { BorderStyle = border, Label = "x" };
        var result = BannerController.NormaliseCustomAnnouncementTheme(input);
        Assert.Equal(expected, result?.BorderStyle);
    }

    [Theory]
    [InlineData("#C9A96E", "#C9A96E")]
    [InlineData("#c9a96e", "#c9a96e")]
    [InlineData("rgba(0,0,0,.68)", "rgba(0,0,0,.68)")]
    [InlineData("rgb(255, 0, 0)", "rgb(255, 0, 0)")]
    [InlineData("rgba(255, 100, 50, 0.5)", "rgba(255, 100, 50, 0.5)")]
    [InlineData("red", null)]                                // CSS keyword not accepted
    [InlineData("hsl(120,50%,50%)", null)]                   // HSL not accepted
    [InlineData("var(--accent)", null)]                      // CSS var not accepted
    [InlineData("rgba(0,0,0,.68);position:fixed", null)]    // CSS injection attempt
    [InlineData("", null)]
    [InlineData(null, null)]
    // v0.6.1: tighter bounds + case-insensitive matching.
    [InlineData("RGBA(255,0,0,.5)", "RGBA(255,0,0,.5)")]      // uppercase now accepted
    [InlineData("Rgba(255,0,0,.5)", "Rgba(255,0,0,.5)")]      // mixed case OK
    [InlineData("rgb(256, 0, 0)", null)]                      // R out of range
    [InlineData("rgb(0, 256, 0)", null)]                      // G out of range
    [InlineData("rgb(0, 0, 999)", null)]                      // B way out of range
    [InlineData("rgba(255, 255, 255, 0.5)", "rgba(255, 255, 255, 0.5)")] // boundary OK
    public void NormaliseCssColor_AcceptsHexAndRgba(string? input, string? expected)
    {
        Assert.Equal(expected, BannerController.NormaliseCssColor(input));
    }

    // ── ValidateSchedules bounds (v0.6.1) ──────────────────────────────────────

    [Fact]
    public void ValidateSchedules_Fixed_InvertedDates_Rejected()
    {
        var sched = new List<BannerSchedule?>
        {
            new() { Type = "fixed", FixedStart = "2026-06-01T00:00:00Z", FixedEnd = "2026-05-01T00:00:00Z" }
        };
        var err = BannerController.ValidateSchedules(sched, "test");
        Assert.NotNull(err);
        Assert.Contains("fixedStart must be strictly before fixedEnd", err);
    }

    [Fact]
    public void ValidateSchedules_Fixed_OpenEndedOk()
    {
        // Only one bound set = open-ended window, allowed.
        var onlyStart = new List<BannerSchedule?>
        {
            new() { Type = "fixed", FixedStart = "2026-06-01T00:00:00Z" }
        };
        Assert.Null(BannerController.ValidateSchedules(onlyStart, "test"));
    }

    [Theory]
    [InlineData(13, 1, 12, 31, "monthStart")]
    [InlineData(1, 32, 12, 31, "dayStart")]
    [InlineData(1, 1, 13, 31, "monthEnd")]
    [InlineData(1, 1, 12, 32, "dayEnd")]
    [InlineData(0, 1, 12, 31, "monthStart")]
    [InlineData(1, 0, 12, 31, "dayStart")]
    public void ValidateSchedules_Annual_OutOfRangeRejected(int ms, int ds, int me, int de, string expectedField)
    {
        var sched = new List<BannerSchedule?>
        {
            new() { Type = "annual", MonthStart = ms, DayStart = ds, MonthEnd = me, DayEnd = de }
        };
        var err = BannerController.ValidateSchedules(sched, "test");
        Assert.NotNull(err);
        Assert.Contains(expectedField, err);
    }

    [Fact]
    public void ValidateSchedules_Annual_ValidWrapAccepted()
    {
        // Christmas range Dec 20 -> Jan 5 wraps the year, that's legal.
        var sched = new List<BannerSchedule?>
        {
            new() { Type = "annual", MonthStart = 12, DayStart = 20, MonthEnd = 1, DayEnd = 5 }
        };
        Assert.Null(BannerController.ValidateSchedules(sched, "test"));
    }

    [Fact]
    public void ValidateSchedules_Weekly_EmptyDaysRejected()
    {
        var sched = new List<BannerSchedule?> { new() { Type = "weekly", WeekDays = new() } };
        var err = BannerController.ValidateSchedules(sched, "test");
        Assert.NotNull(err);
        Assert.Contains("at least one day", err);
    }

    [Fact]
    public void ValidateSchedules_Weekly_NullDaysRejected()
    {
        var sched = new List<BannerSchedule?> { new() { Type = "weekly", WeekDays = null! } };
        var err = BannerController.ValidateSchedules(sched, "test");
        Assert.NotNull(err);
        Assert.Contains("at least one day", err);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(99)]
    public void ValidateSchedules_Weekly_OutOfRangeDayRejected(int badDay)
    {
        var sched = new List<BannerSchedule?>
        {
            new() { Type = "weekly", WeekDays = new() { 1, badDay, 3 } }
        };
        var err = BannerController.ValidateSchedules(sched, "test");
        Assert.NotNull(err);
        Assert.Contains("0..6", err);
    }

    [Fact]
    public void ValidateSchedules_Daily_NoStructuralConstraint()
    {
        // Daily schedules don't have month/day bounds, so we just accept any time string.
        var sched = new List<BannerSchedule?>
        {
            new() { Type = "daily", TimeStart = "09:00", TimeEnd = "17:00" },
            new() { Type = "daily" } // no times set is fine too (matches everything)
        };
        Assert.Null(BannerController.ValidateSchedules(sched, "test"));
    }

    [Theory]
    [InlineData("velours", "velours")]
    [InlineData("oled", "oled")]
    [InlineData("neon", "neon")]
    [InlineData("glass", "glass")]
    [InlineData("UNKNOWN", null)] // unknown override -> drop (inherit global)
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void NormaliseAnnouncementThemeOverride_AcceptsFourThemesElseNull(string? input, string? expected)
    {
        Assert.Equal(expected, BannerController.NormaliseAnnouncementThemeOverride(input));
    }

    [Theory]
    [InlineData("off", "off")]
    [InlineData("slow", "slow")]
    [InlineData("normal", "normal")]
    [InlineData("fast", "fast")]
    [InlineData("garbage", "normal")]
    [InlineData("", "normal")]
    [InlineData(null, "normal")]
    public void NormaliseAnimationSpeed_WhitelistsKnownValues(string? input, string expected)
    {
        Assert.Equal(expected, BannerController.NormaliseAnimationSpeed(input));
    }

    [Theory]
    [InlineData("none", "none")]
    [InlineData("low", "low")]
    [InlineData("normal", "normal")]
    [InlineData("dense", "dense")]
    [InlineData("xxx", "normal")]
    [InlineData(null, "normal")]
    public void NormaliseParticleDensity_WhitelistsKnownValues(string? input, string expected)
    {
        Assert.Equal(expected, BannerController.NormaliseParticleDensity(input));
    }

    [Theory]
    [InlineData("full", "full")]
    [InlineData("rotating", "rotating")]
    [InlineData("simple", "simple")]
    [InlineData("none", "none")]
    [InlineData("bogus", "full")]
    [InlineData(null, "full")]
    public void NormaliseBorderStyle_WhitelistsKnownValues(string? input, string expected)
    {
        Assert.Equal(expected, BannerController.NormaliseBorderStyle(input));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("HTTPS://example.com", true)]
    [InlineData("/relative", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,foo", false)]
    [InlineData("vbscript:msgbox", false)]
    [InlineData("ftp://example.com", false)]
    // Protocol-relative URLs must be rejected — they navigate to an arbitrary host
    // when used in href, bypassing the http(s)/relative-path allowlist intent.
    [InlineData("//evil.com", false)]
    [InlineData("//evil.com/path", false)]
    [InlineData("///triple-slash", false)]
    public void IsUrlSafe_RejectsDangerousSchemes(string? input, bool expected)
    {
        Assert.Equal(expected, BannerController.IsUrlSafe(input));
    }

    [Theory]
    [InlineData("#C9A96E", "#FFFFFF", "#C9A96E")]
    [InlineData("#c9a96e", "#FFFFFF", "#c9a96e")]
    [InlineData("not-a-color", "#FFFFFF", "#FFFFFF")]
    [InlineData("red;position:fixed;top:0", "#FFFFFF", "#FFFFFF")]
    [InlineData("", "#FFFFFF", "#FFFFFF")]
    [InlineData(null, "#FFFFFF", "#FFFFFF")]
    [InlineData("#FFF", "#FFFFFF", "#FFFFFF")] // shorthand not accepted
    public void NormaliseHexColorOrDefault_FallsBackOnInvalid(string? input, string fallback, string expected)
    {
        Assert.Equal(expected, BannerController.NormaliseHexColorOrDefault(input, fallback));
    }

    [Fact]
    public void NormaliseReleaseNotes_HandlesNullAndCapsLength()
    {
        Assert.Empty(BannerController.NormaliseReleaseNotes(null));

        var oversized = new List<ReleaseNoteSection>();
        for (var i = 0; i < 30; i++)
            oversized.Add(new ReleaseNoteSection { Title = $"T{i}", Body = $"B{i}", Icon = "✨" });
        var capped = BannerController.NormaliseReleaseNotes(oversized);
        Assert.Equal(20, capped.Count);
    }

    [Fact]
    public void NormaliseReleaseNotes_SkipsCompletelyEmptySections()
    {
        var input = new List<ReleaseNoteSection>
        {
            new() { Title = "Real", Body = "Body", Icon = "✨" },
            new() { Title = "", Body = "", Icon = "" },
            new() { Title = "  ", Body = "  ", Icon = "  " }
        };
        var result = BannerController.NormaliseReleaseNotes(input);
        Assert.Single(result);
        Assert.Equal("Real", result[0].Title);
    }

    [Theory]
    [InlineData("Hello", 100, "Hello")]
    [InlineData("  Hello  ", 100, "Hello")]
    [InlineData("", 100, null)]
    [InlineData("   ", 100, null)]
    [InlineData(null, 100, null)]
    [InlineData("LongString", 4, "Long")]
    public void NormaliseOptionalString_TrimsAndTruncates(string? input, int max, string? expected)
    {
        Assert.Equal(expected, BannerController.NormaliseOptionalString(input, max));
    }

    [Theory]
    [InlineData("/foo", true, null)]                       // ok
    [InlineData("/foo/bar*", true, null)]                  // wildcard ok
    [InlineData("/foo?x=y", true, null)]                   // query chars ok
    [InlineData("foo bar", false, "Invalid route pattern")] // space rejected by character class
    [InlineData("/foo/../bar", false, "consecutive")]       // .. rejected (hygiene)
    [InlineData("/foo//bar", false, "consecutive")]         // // rejected (hygiene)
    [InlineData("../etc/passwd", false, "consecutive")]     // .. rejected
    public void ValidateRoutes_RejectsBadPatterns(string pattern, bool valid, string? expectedFragment)
    {
        var lists = new List<List<string>?> { new List<string> { pattern } };
        var err = BannerController.ValidateRoutes(lists, "test");
        if (valid)
        {
            Assert.Null(err);
        }
        else
        {
            Assert.NotNull(err);
            if (expectedFragment is not null)
                Assert.Contains(expectedFragment, err);
        }
    }

    [Fact]
    public void ValidateRoutes_LongPatternRejected()
    {
        var lists = new List<List<string>?> { new List<string> { new string('a', 513) } };
        var err = BannerController.ValidateRoutes(lists, "test");
        Assert.NotNull(err);
        Assert.Contains("512", err);
    }

    [Theory]
    [InlineData("discord.com", true)]
    [InlineData("DISCORD.COM", true)]
    [InlineData("discordapp.com", true)]
    [InlineData("hooks.slack.com", true)]
    [InlineData("HOOKS.SLACK.COM", true)]
    [InlineData("example.com", false)]
    [InlineData("notslack.com", false)]
    [InlineData("evil.discord.com.attacker.tld", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsKnownWebhookHost_RecognisesProviders(string? host, bool expected)
    {
        Assert.Equal(expected, BannerController.IsKnownWebhookHost(host));
    }
}
