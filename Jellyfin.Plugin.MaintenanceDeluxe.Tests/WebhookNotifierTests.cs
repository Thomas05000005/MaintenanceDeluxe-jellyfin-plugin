using Xunit;

namespace Jellyfin.Plugin.MaintenanceDeluxe.Tests;

public class WebhookNotifierTests
{
    [Theory]
    [InlineData("https://discord.com/api/webhooks/123/abc", WebhookFormat.Discord)]
    [InlineData("https://discordapp.com/api/webhooks/123/abc", WebhookFormat.Discord)]
    [InlineData("https://DISCORD.COM/api/webhooks/anything", WebhookFormat.Discord)]
    [InlineData("https://hooks.slack.com/services/T00/B00/xxx", WebhookFormat.Slack)]
    [InlineData("https://HOOKS.SLACK.COM/services/T00/B00/xxx", WebhookFormat.Slack)]
    [InlineData("https://example.com/webhook", WebhookFormat.Generic)]
    [InlineData("https://hooks.notslack.com/services/foo", WebhookFormat.Generic)]
    [InlineData("", WebhookFormat.Generic)]
    [InlineData(null, WebhookFormat.Generic)]
    [InlineData("   ", WebhookFormat.Generic)]
    public void DetectFormat_ClassifiesUrlsCorrectly(string? url, WebhookFormat expected)
    {
        Assert.Equal(expected, WebhookNotifier.DetectFormat(url));
    }
}
