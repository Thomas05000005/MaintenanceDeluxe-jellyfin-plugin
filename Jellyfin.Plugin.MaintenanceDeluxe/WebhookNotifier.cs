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
    Restarting,
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

    /// <summary>Auto-detects the payload format based on URL host (v0.7.0: proper URI parsing
    /// instead of substring match). Substring matching used to false-positive on URLs like
    /// "https://my-relay.com/discord-bot/api/webhooks/..." which would get a Discord payload
    /// shape that the relay's parser doesn't understand. Host comparison fixes that.</summary>
    internal static WebhookFormat DetectFormat(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return WebhookFormat.Generic;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return WebhookFormat.Generic;
        var host = uri.Host;
        if (host.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("ptb.discord.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("canary.discord.com", StringComparison.OrdinalIgnoreCase))
            return WebhookFormat.Discord;
        if (host.Equals("hooks.slack.com", StringComparison.OrdinalIgnoreCase))
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
        if (evt == WebhookEvent.Restarting && !settings.NotifyOnRestart) return;

        try
        {
            var format = DetectFormat(settings.Url);
            var payload = BuildPayload(format, evt, maintenance);
            await SendAsync(settings.Url!, payload, httpFactory, logger, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Intentionally broad: a webhook failure must never block maintenance.
            // v0.7.0: log only ex.GetType().Name + Message (not the full exception which can
            // contain the webhook URL — and the URL itself contains a Discord/Slack token).
            logger?.LogWarning(
                "Webhook notification failed for event {Event}: {ExType}: {ExMessage}",
                evt, ex.GetType().Name, SanitiseExceptionMessage(ex.Message, settings.Url));
        }
    }

    /// <summary>v0.7.0: strips webhook URLs / hostnames from exception messages before logging
    /// so the log file (often viewed by less-privileged users) doesn't leak the token-bearing
    /// URL. We replace any substring of the configured URL with `[redacted]`.</summary>
    private static string SanitiseExceptionMessage(string? message, string? url)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        if (string.IsNullOrEmpty(url)) return message;
        var redacted = message.Replace(url, "[redacted-webhook-url]", StringComparison.OrdinalIgnoreCase);
        // Also strip the host substring on its own, since some HTTP exceptions only quote the host.
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            redacted = redacted.Replace(uri.Host, "[redacted-host]", StringComparison.OrdinalIgnoreCase);
        return redacted;
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
            // v0.7.0: same sanitisation as NotifyAsync — don't surface the URL in logs.
            // The body returned to the admin UI also gets a generic message; if the admin
            // needs the actual error, it's in Jellyfin server logs (host blocked there too).
            logger?.LogWarning(
                "Test webhook failed: {ExType}: {ExMessage}",
                ex.GetType().Name, SanitiseExceptionMessage(ex.Message, url));
            // Return a generic message rather than the raw exception (which can leak internal
            // hostnames via SSL cert subject names, "Cannot connect to internal-api.local"...).
            return (0, $"Webhook test failed: {ex.GetType().Name}. Check Jellyfin server logs for details.");
        }
    }

    // v0.7.0: Discord embed total has a hard 6000-char limit. We cap before send so the
    // request doesn't fail server-side with a cryptic 400. Same general guard for Slack
    // (40000 chars block limit) and generic webhooks (~64KB common reverse-proxy cap).
    private const int MaxPayloadBytes = 32 * 1024; // 32KB — safely below Discord/Slack/proxy limits

    private static async Task<(int StatusCode, string Body)> SendAsync(
        string url,
        object payload,
        IHttpClientFactory httpFactory,
        ILogger? logger,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, _json);
        if (json.Length > MaxPayloadBytes)
        {
            logger?.LogWarning(
                "Webhook payload exceeds {Max} bytes (got {Got}); refusing to send to avoid HTTP 400.",
                MaxPayloadBytes, json.Length);
            return (0, $"Payload too large ({json.Length} bytes); max {MaxPayloadBytes}. Trim customTitle/customSubtitle/message.");
        }

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

                // v0.7.0: respect 429 Retry-After header per Discord/Slack docs.
                // If the header asks us to wait longer than 4s, we skip the retry rather than
                // burning the next webhook attempt — caller can retry later.
                if (status == 429 && attempt == 1)
                {
                    var retryAfter = TryGetRetryAfterSeconds(resp);
                    logger?.LogDebug("Webhook rate-limited (429), Retry-After={RetryAfter}s.", retryAfter);
                    if (retryAfter > 0 && retryAfter <= 4)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(retryAfter), ct).ConfigureAwait(false);
                        continue;
                    }
                    // Too long to wait or no header: don't retry, surface the rate-limit to the caller.
                    return (status, body);
                }
                if (status >= 500 && attempt == 1)
                {
                    logger?.LogDebug("Webhook returned {Status}, retrying once.", status);
                    continue;
                }
                return (status, body);
            }
            catch (TaskCanceledException) when (attempt == 1)
            {
                logger?.LogDebug("Webhook timed out, retrying once.");
                continue;
            }
            catch (HttpRequestException ex) when (attempt == 1)
            {
                logger?.LogDebug(ex, "Webhook transport error, retrying once.");
                continue;
            }
        }
        return (0, "Webhook unreachable after 2 attempts.");
    }

    /// <summary>v0.7.0: parse the Retry-After header (seconds form or HTTP-date form).
    /// Returns 0 if absent or unparseable.</summary>
    private static int TryGetRetryAfterSeconds(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter is null) return 0;
        if (resp.Headers.RetryAfter.Delta is TimeSpan delta) return (int)Math.Min(delta.TotalSeconds, 60);
        if (resp.Headers.RetryAfter.Date is DateTimeOffset date)
        {
            var secs = (date - DateTimeOffset.UtcNow).TotalSeconds;
            return (int)Math.Min(Math.Max(secs, 0), 60);
        }
        return 0;
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
        WebhookEvent.Restarting => (
            "🔁 Redémarrage en cours",
            "Le serveur Jellyfin redémarre, retour imminent.",
            0xE57373,
            "server_restarting"),
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
