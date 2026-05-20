using System;
using System.Net.Http;
using System.Net.Http.Headers;
using Jellyfin.Plugin.MaintenanceDeluxe.Api;
using Xunit;

namespace Jellyfin.Plugin.MaintenanceDeluxe.Tests;

public class WebhookNotifierTests
{
    [Theory]
    [InlineData("https://discord.com/api/webhooks/123/abc", WebhookFormat.Discord)]
    [InlineData("https://discordapp.com/api/webhooks/123/abc", WebhookFormat.Discord)]
    [InlineData("https://DISCORD.COM/api/webhooks/anything", WebhookFormat.Discord)]
    [InlineData("https://ptb.discord.com/api/webhooks/123/abc", WebhookFormat.Discord)]
    [InlineData("https://canary.discord.com/api/webhooks/123/abc", WebhookFormat.Discord)]
    [InlineData("https://hooks.slack.com/services/T00/B00/xxx", WebhookFormat.Slack)]
    [InlineData("https://HOOKS.SLACK.COM/services/T00/B00/xxx", WebhookFormat.Slack)]
    [InlineData("https://example.com/webhook", WebhookFormat.Generic)]
    [InlineData("https://hooks.notslack.com/services/foo", WebhookFormat.Generic)]
    // v0.7.0: substring-match regression guards. These all USED to be classified incorrectly
    // because the old impl matched "discord.com/api/webhooks/" anywhere in the URL.
    [InlineData("https://relay.example.com/discord.com/api/webhooks/proxy", WebhookFormat.Generic)]
    [InlineData("https://my-discord-bot.example.com/api/webhooks/x", WebhookFormat.Generic)]
    [InlineData("https://attacker.com/hooks.slack.com/services/abc", WebhookFormat.Generic)]
    [InlineData("", WebhookFormat.Generic)]
    [InlineData(null, WebhookFormat.Generic)]
    [InlineData("   ", WebhookFormat.Generic)]
    [InlineData("not-a-url", WebhookFormat.Generic)]
    public void DetectFormat_ClassifiesUrlsCorrectly(string? url, WebhookFormat expected)
    {
        Assert.Equal(expected, WebhookNotifier.DetectFormat(url));
    }

    // ── SSRF defense (v0.7.0) ──────────────────────────────────────────────────

    [Theory]
    [InlineData("https://discord.com/api/webhooks/123/abc", true)]
    [InlineData("https://hooks.slack.com/services/T00/B00/xxx", true)]
    [InlineData("https://example.com/webhook", true)]
    public void IsWebhookHostSafe_AcceptsLegitPublicHosts(string url, bool expectedSafe)
    {
        var (safe, _) = BannerController.IsWebhookHostSafe(url);
        Assert.Equal(expectedSafe, safe);
    }

    [Theory]
    [InlineData("https://127.0.0.1/hook")]        // IPv4 loopback
    [InlineData("https://localhost/hook")]         // localhost literal
    [InlineData("https://LOCALHOST/hook")]         // case-insensitive
    [InlineData("https://[::1]/hook")]             // IPv6 loopback
    [InlineData("https://169.254.169.254/")]       // AWS metadata
    [InlineData("https://169.254.170.2/")]         // Azure metadata
    [InlineData("https://10.0.0.1/internal")]      // RFC1918 10.0.0.0/8
    [InlineData("https://10.255.255.255/")]
    [InlineData("https://192.168.0.1/router")]     // RFC1918 192.168/16
    [InlineData("https://172.16.0.1/")]            // RFC1918 172.16/12 lower
    [InlineData("https://172.31.0.1/")]            // RFC1918 172.16/12 upper
    [InlineData("https://0.0.0.0/")]               // 0.0.0.0/8
    [InlineData("https://my-server.local/")]       // .local TLD
    [InlineData("https://something.internal/")]    // .internal TLD
    [InlineData("https://something.localhost/")]   // .localhost TLD
    public void IsWebhookHostSafe_BlocksPrivateAndLoopbackHosts(string url)
    {
        var (safe, reason) = BannerController.IsWebhookHostSafe(url);
        Assert.False(safe, $"Expected '{url}' to be refused but was accepted.");
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("http://discord.com/")]           // http rejected
    [InlineData("not-a-url")]
    [InlineData("")]
    [InlineData("ftp://example.com/")]
    public void IsWebhookHostSafe_BlocksMalformedAndNonHttps(string url)
    {
        var (safe, _) = BannerController.IsWebhookHostSafe(url);
        Assert.False(safe);
    }

    [Theory]
    [InlineData("https://172.15.0.1/", true)]   // 172.15.x.x is NOT private (only 172.16-31)
    [InlineData("https://172.32.0.1/", true)]   // 172.32.x.x is NOT private
    [InlineData("https://11.0.0.1/", true)]     // 11.x.x.x is NOT private (only 10.x)
    [InlineData("https://192.169.0.1/", true)]  // 192.169.x.x is NOT private (only 192.168)
    public void IsWebhookHostSafe_AcceptsBoundaryPublicRanges(string url, bool expectedSafe)
    {
        var (safe, _) = BannerController.IsWebhookHostSafe(url);
        Assert.Equal(expectedSafe, safe);
    }

    // ── SanitiseExceptionMessage (v0.8.0) ──────────────────────────────────────
    // The exception message logged after a webhook failure must never echo the
    // full webhook URL (it contains a Discord/Slack token) nor the bare host
    // (the host alone is enough for an attacker to know which provider you're
    // using). Each row pins one of those redactions.

    [Theory]
    [InlineData("Failed: https://hooks.slack.com/services/T001/B001/XXX", "https://hooks.slack.com/services/T001/B001/XXX", "Failed: [redacted-webhook-url]")]
    [InlineData("Cannot connect to hooks.slack.com", "https://hooks.slack.com/services/T001/B001/XXX", "Cannot connect to [redacted-host]")]
    [InlineData("Generic error message", "https://hooks.slack.com/services/T001/B001/XXX", "Generic error message")]
    [InlineData("", "https://hooks.slack.com/services/X", "")]
    [InlineData(null, "https://hooks.slack.com/services/X", "")]
    [InlineData("Something failed", null, "Something failed")]
    [InlineData("Something failed", "", "Something failed")]
    public void SanitiseExceptionMessage_RedactsUrlAndHost(string? message, string? url, string expected)
    {
        Assert.Equal(expected, WebhookNotifier.SanitiseExceptionMessage(message, url));
    }

    // ── TryGetRetryAfterSeconds (v0.8.0) ───────────────────────────────────────
    // 60s cap is intentional: we don't want a misbehaving provider to be able to
    // stall the activation flow for an arbitrary amount of time via Retry-After.

    [Fact]
    public void TryGetRetryAfterSeconds_NullResponse_Returns0()
    {
        using var resp = new HttpResponseMessage();
        Assert.Equal(0, WebhookNotifier.TryGetRetryAfterSeconds(resp));
    }

    [Fact]
    public void TryGetRetryAfterSeconds_DeltaCapped60s()
    {
        using var resp = new HttpResponseMessage();
        resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
        var result = WebhookNotifier.TryGetRetryAfterSeconds(resp);
        Assert.True(result <= 60);
        Assert.True(result > 0);
    }

    [Fact]
    public void TryGetRetryAfterSeconds_DeltaWithinLimit()
    {
        using var resp = new HttpResponseMessage();
        resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(4));
        Assert.Equal(4, WebhookNotifier.TryGetRetryAfterSeconds(resp));
    }

    [Fact]
    public void TryGetRetryAfterSeconds_PastDateReturns0()
    {
        using var resp = new HttpResponseMessage();
        resp.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(0, WebhookNotifier.TryGetRetryAfterSeconds(resp));
    }
}
