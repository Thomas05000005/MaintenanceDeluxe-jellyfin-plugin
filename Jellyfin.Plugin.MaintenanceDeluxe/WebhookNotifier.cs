using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MaintenanceDeluxe.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MaintenanceDeluxe;

/// <summary>The detected webhook payload flavour.</summary>
public enum WebhookFormat
{
    /// <summary>Discord embed-style payload.</summary>
    Discord,
    /// <summary>Slack Block Kit payload.</summary>
    Slack,
    /// <summary>Plain JSON payload for arbitrary integrations.</summary>
    Generic
}

/// <summary>The notification trigger.</summary>
internal enum WebhookEvent
{
    Activated,
    Deactivated,
    Test
}

/// <summary>
/// Sends maintenance notifications to a Discord, Slack, or generic webhook.
/// All public methods catch every exception — a misconfigured webhook must NEVER
/// crash the activation/deactivation flow.
/// </summary>
internal static class WebhookNotifier
{
    private static readonly TimeSpan _httpTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    /// <summary>Auto-detects the payload format based on URL host.</summary>
    internal static WebhookFormat DetectFormat(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return WebhookFormat.Generic;
        if (url.Contains("discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("discordapp.com/api/webhooks/", StringComparison.OrdinalIgnoreCase))
            return WebhookFormat.Discord;
        if (url.Contains("hooks.slack.com/services/", StringComparison.OrdinalIgnoreCase))
            return WebhookFormat.Slack;
        return WebhookFormat.Generic;
    }

    /// <summary>Sends a notification for a real activation/deactivation event. Never throws.</summary>
    internal static async Task NotifyAsync(
        WebhookSettings settings,
        WebhookEvent evt,
        MaintenanceSetting maintenance,
        IHttpClientFactory httpFactory,
        ILogger? logger,
        CancellationToken ct = default)
    {
        if (settings is null || !settings.Enabled || string.IsNullOrWhiteSpace(settings.Url)) return;
        if (evt == WebhookEvent.Activated && !settings.NotifyOnActivate) return;
        if (evt == WebhookEvent.Deactivated && !settings.NotifyOnDeactivate) return;

        try
        {
            var format = DetectFormat(settings.Url);
            var payload = BuildPayload(format, evt, maintenance);
            await SendAsync(settings.Url!, payload, httpFactory, logger, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Intentionally broad: a webhook failure must never block maintenance.
            logger?.LogWarning(ex, "[MaintenanceDeluxe] Webhook notification failed for event {Event}.", evt);
        }
    }

    /// <summary>Sends a test payload. Returns (statusCode, responseBody) for the admin UI to display.
    /// Never throws — exceptions are surfaced as a synthetic 0 status with the exception message as body.</summary>
    internal static async Task<(int StatusCode, string Body)> TestAsync(
        string url,
        IHttpClientFactory httpFactory,
        ILogger? logger,
        CancellationToken ct = default)
    {
        try
        {
            var format = DetectFormat(url);
            var payload = BuildTestPayload(format);
            return await SendAsync(url, payload, httpFactory, logger, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[MaintenanceDeluxe] Test webhook failed.");
            return (0, ex.Message);
        }
    }

    private static async Task<(int StatusCode, string Body)> SendAsync(
        string url,
        object payload,
        IHttpClientFactory httpFactory,
        ILogger? logger,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, _json);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var client = httpFactory.CreateClient("MaintenanceDeluxe.Webhook");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_httpTimeout);

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await client.PostAsync(url, content, cts.Token).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var status = (int)resp.StatusCode;

                if (status >= 500 && attempt == 1)
                {
                    logger?.LogDebug("[MaintenanceDeluxe] Webhook returned {Status}, retrying once.", status);
                    continue;
                }
                return (status, body);
            }
            catch (TaskCanceledException) when (attempt == 1)
            {
                logger?.LogDebug("[MaintenanceDeluxe] Webhook timed out, retrying once.");
                continue;
            }
            catch (HttpRequestException ex) when (attempt == 1)
            {
                logger?.LogDebug(ex, "[MaintenanceDeluxe] Webhook transport error, retrying once.");
                continue;
            }
        }
        return (0, "Webhook unreachable after 2 attempts.");
    }

    // ─────────── Payload builders ───────────

    private static object BuildPayload(WebhookFormat format, WebhookEvent evt, MaintenanceSetting m) => format switch
    {
        WebhookFormat.Discord => BuildDiscordPayload(evt, m),
        WebhookFormat.Slack => BuildSlackPayload(evt, m),
        _ => BuildGenericPayload(evt, m)
    };

    private static object BuildTestPayload(WebhookFormat format) => format switch
    {
        WebhookFormat.Discord => new
        {
            embeds = new[]
            {
                new
                {
                    title = "✅ Test MaintenanceDeluxe",
                    description = "Si tu vois ce message, ton webhook Discord est correctement configuré.",
                    color = 0x4CAF50,
                    timestamp = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                }
            }
        },
        WebhookFormat.Slack => new
        {
            blocks = new object[]
            {
                new { type = "header", text = new { type = "plain_text", text = "✅ Test MaintenanceDeluxe" } },
                new { type = "section", text = new { type = "mrkdwn", text = "Si tu vois ce message, ton webhook Slack est correctement configuré." } }
            }
        },
        _ => new
        {
            @event = "maintenance_test",
            message = "Test webhook MaintenanceDeluxe",
            timestamp = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
        }
    };

    private static (string Title, string Description, int Color, string EventCode) GetEventMeta(WebhookEvent evt, MaintenanceSetting m) => evt switch
    {
        WebhookEvent.Activated => (
            $"🔧 {(string.IsNullOrWhiteSpace(m.CustomTitle) ? "Maintenance activée" : m.CustomTitle)}",
            string.IsNullOrWhiteSpace(m.CustomSubtitle) ? (m.Message ?? string.Empty) : m.CustomSubtitle!,
            0xC9A96E,
            "maintenance_activated"),
        WebhookEvent.Deactivated => (
            "✅ Maintenance terminée",
            "Le serveur est de nouveau accessible.",
            0x4CAF50,
            "maintenance_deactivated"),
        _ => ("MaintenanceDeluxe", "Notification", 0x808080, "maintenance_event")
    };

    private static object BuildDiscordPayload(WebhookEvent evt, MaintenanceSetting m)
    {
        var (title, desc, color, _) = GetEventMeta(evt, m);
        var fields = new System.Collections.Generic.List<object>();

        if (m.ScheduledEnd.HasValue)
            fields.Add(new { name = "Fin programmée", value = FormatUtc(m.ScheduledEnd.Value), inline = true });
        if (m.ScheduledRestart.HasValue)
            fields.Add(new { name = "Redémarrage prévu", value = FormatUtc(m.ScheduledRestart.Value), inline = true });
        if (evt == WebhookEvent.Activated)
        {
            fields.Add(new { name = "Utilisateurs désactivés", value = m.MaintenanceDisabledUserIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), inline = true });
            if (m.WhitelistedUserIds.Count > 0)
                fields.Add(new { name = "Whitelistés", value = m.WhitelistedUserIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), inline = true });
        }
        if (!string.IsNullOrWhiteSpace(m.StatusUrl))
            fields.Add(new { name = "Page de statut", value = m.StatusUrl, inline = false });

        return new
        {
            embeds = new[]
            {
                new
                {
                    title,
                    description = desc,
                    color,
                    fields = fields.ToArray(),
                    timestamp = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                }
            }
        };
    }

    private static object BuildSlackPayload(WebhookEvent evt, MaintenanceSetting m)
    {
        var (title, desc, _, _) = GetEventMeta(evt, m);
        var blocks = new System.Collections.Generic.List<object>
        {
            new { type = "header", text = new { type = "plain_text", text = title } },
            new { type = "section", text = new { type = "mrkdwn", text = desc } }
        };

        var fieldList = new System.Collections.Generic.List<object>();
        if (m.ScheduledEnd.HasValue)
            fieldList.Add(new { type = "mrkdwn", text = $"*Fin :*\n{FormatUtc(m.ScheduledEnd.Value)}" });
        if (m.ScheduledRestart.HasValue)
            fieldList.Add(new { type = "mrkdwn", text = $"*Redémarrage :*\n{FormatUtc(m.ScheduledRestart.Value)}" });
        if (evt == WebhookEvent.Activated)
        {
            fieldList.Add(new { type = "mrkdwn", text = $"*Désactivés :*\n{m.MaintenanceDisabledUserIds.Count}" });
            if (m.WhitelistedUserIds.Count > 0)
                fieldList.Add(new { type = "mrkdwn", text = $"*Whitelistés :*\n{m.WhitelistedUserIds.Count}" });
        }

        if (fieldList.Count > 0)
            blocks.Add(new { type = "section", fields = fieldList.ToArray() });
        if (!string.IsNullOrWhiteSpace(m.StatusUrl))
            blocks.Add(new { type = "section", text = new { type = "mrkdwn", text = $"<{m.StatusUrl}|Page de statut>" } });

        return new { blocks = blocks.ToArray() };
    }

    private static object BuildGenericPayload(WebhookEvent evt, MaintenanceSetting m)
    {
        var (title, desc, _, code) = GetEventMeta(evt, m);
        return new
        {
            @event = code,
            title,
            description = desc,
            isActive = m.IsActive,
            scheduledStart = m.ScheduledStart?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            scheduledEnd = m.ScheduledEnd?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            scheduledRestart = m.ScheduledRestart?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            activatedAt = m.ActivatedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            disabledUserCount = m.MaintenanceDisabledUserIds.Count,
            whitelistedUserCount = m.WhitelistedUserIds.Count,
            statusUrl = m.StatusUrl,
            timestamp = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static string FormatUtc(DateTime dt) =>
        dt.ToUniversalTime().ToString("dd/MM/yyyy HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
}
