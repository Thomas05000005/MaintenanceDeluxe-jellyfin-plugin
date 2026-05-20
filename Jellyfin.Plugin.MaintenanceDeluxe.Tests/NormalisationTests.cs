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
    [InlineData("UNKNOWN", "velours")]
    [InlineData("", "velours")]
    [InlineData(null, "velours")]
    public void NormaliseAnnouncementTheme_AcceptsFourThemesElseVelours(string? input, string expected)
    {
        Assert.Equal(expected, BannerController.NormaliseAnnouncementTheme(input));
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
