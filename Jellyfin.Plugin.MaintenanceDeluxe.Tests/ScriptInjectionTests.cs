using Jellyfin.Plugin.MaintenanceDeluxe.Web;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.MaintenanceDeluxe.Tests;

/// <summary>
/// Unit tests for the standalone script-injection logic (suite 1.0). The middleware's HTTP plumbing
/// is proven end-to-end by the CI smoke test against a real Jellyfin container; these tests pin the
/// pure decisions it makes: WHICH requests get buffered, and HOW the tag lands in the document.
/// </summary>
public class ScriptInjectionTests
{
    private const string Tag = "<script id=\"maintenancedeluxe-suite\" src=\"/MaintenanceDeluxe/banner.js?v=1.0.0.0\" defer></script>";

    [Theory]
    [InlineData("", true)]                        // "/" arrives as an empty PathString
    [InlineData("/", true)]
    [InlineData("/web", true)]
    [InlineData("/web/", true)]
    [InlineData("/web/index.html", true)]
    [InlineData("/WEB/INDEX.HTML", true)]          // case-insensitive
    // Everything else must stream through untouched — never buffer API payloads or media.
    [InlineData("/System/Info/Public", false)]
    [InlineData("/Items/abc/Images/Primary", false)]
    [InlineData("/web/main.bundle.js", false)]
    [InlineData("/web/assets/style.css", false)]
    [InlineData("/videos/1/stream.mkv", false)]
    [InlineData("/MaintenanceDeluxe/banner.js", false)]
    public void IsWebUiDocumentPath_OnlyMatchesTheSpaShell(string path, bool expected)
    {
        Assert.Equal(expected, ScriptInjectionMiddleware.IsWebUiDocumentPath(new PathString(path)));
    }

    [Fact]
    public void InjectIntoHtml_InsertsBeforeClosingBody()
    {
        var html = "<html><head></head><body><div id=\"app\"></div></body></html>";
        var result = ScriptInjectionMiddleware.InjectIntoHtml(html, Tag);
        Assert.Contains(Tag, result);
        // Placed INSIDE body, immediately before the closing tag.
        Assert.EndsWith(Tag + "</body></html>", result);
    }

    [Fact]
    public void InjectIntoHtml_FallsBackToClosingHtmlWhenNoBody()
    {
        var html = "<html><head></head></html>";
        var result = ScriptInjectionMiddleware.InjectIntoHtml(html, Tag);
        Assert.EndsWith(Tag + "</html>", result);
    }

    [Fact]
    public void InjectIntoHtml_AppendsWhenNeitherTagPresent()
    {
        var result = ScriptInjectionMiddleware.InjectIntoHtml("<div>fragment</div>", Tag);
        Assert.EndsWith(Tag, result);
    }

    [Fact]
    public void InjectIntoHtml_IsIdempotent_NoDoubleLoadWithLegacyInjector()
    {
        // The legacy JS Injector path may already have added our script to the document; injecting
        // again would load the whole client twice (duplicate overlays, doubled listeners).
        var html = "<html><body>" + Tag + "</body></html>";
        Assert.Equal(html, ScriptInjectionMiddleware.InjectIntoHtml(html, Tag));
    }

    [Fact]
    public void InjectIntoHtml_UsesLastClosingBody()
    {
        // A literal "</body>" inside inline content must not win over the real document end.
        var html = "<html><body><script>var s='</body>';</script></body></html>";
        var result = ScriptInjectionMiddleware.InjectIntoHtml(html, Tag);
        Assert.EndsWith(Tag + "</body></html>", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void InjectIntoHtml_HandlesEmptyInput(string? html)
    {
        Assert.Equal(html, ScriptInjectionMiddleware.InjectIntoHtml(html!, Tag));
    }

    [Fact]
    public void BuildScriptTag_CarriesVersionAndDeferAndId()
    {
        var tag = ScriptInjectionMiddleware.BuildScriptTag("1.2.3.4");
        Assert.Contains("id=\"maintenancedeluxe-suite\"", tag);
        Assert.Contains("/MaintenanceDeluxe/banner.js?v=1.2.3.4", tag);   // cache-busted per version
        Assert.Contains("defer", tag);                                     // never block the SPA boot
    }
}
