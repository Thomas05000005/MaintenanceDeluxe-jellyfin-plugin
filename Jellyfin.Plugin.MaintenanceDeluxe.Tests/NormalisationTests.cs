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
    public void IsUrlSafe_RejectsDangerousSchemes(string? input, bool expected)
    {
        Assert.Equal(expected, BannerController.IsUrlSafe(input));
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
}
