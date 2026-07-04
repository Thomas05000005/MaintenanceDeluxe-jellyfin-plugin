using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MaintenanceDeluxe.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MaintenanceDeluxe;

/// <summary>
/// Registers the named <c>HttpClient</c> ("MaintenanceDeluxe.Webhook") used for outbound webhook
/// delivery with a hardened primary handler. Before this existed the named client was never
/// configured, so <see cref="System.Net.Http.IHttpClientFactory"/> handed out the framework
/// DEFAULT handler whose <c>AllowAutoRedirect</c> is <c>true</c> — which let an allowlisted public
/// host bounce the request into loopback / RFC1918 / cloud-metadata via a single 30x redirect,
/// fully bypassing <see cref="BannerController.IsWebhookHostSafe"/> (SSRF).
///
/// The hardened handler:
/// <list type="number">
/// <item>disables automatic redirect following (a 3xx becomes a terminal non-success), and</item>
/// <item>installs a <c>ConnectCallback</c> that resolves the target host and re-validates EVERY
/// candidate IP against <see cref="BannerController.IsIpAddressSafeToCall"/> before connecting —
/// defeating DNS rebinding and IPv4-mapped-IPv6 / trailing-dot tricks that a name-only check
/// cannot catch.</item>
/// </list>
/// Jellyfin discovers this type and calls <see cref="RegisterServices"/> during DI startup.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>The named client key resolved by <c>IHttpClientFactory.CreateClient(...)</c> in
    /// <see cref="WebhookNotifier"/>. Must stay in sync with that call site.</summary>
    public const string WebhookClientName = "MaintenanceDeluxe.Webhook";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        serviceCollection.AddHttpClient(WebhookClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // (1) A redirect can no longer relocate the request to an internal host.
                AllowAutoRedirect = false,
                // (2) Re-validate the real connection IP, defeating DNS rebinding.
                ConnectCallback = SafeConnectAsync,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            });
    }

    /// <summary>Custom connect path: resolve the host, refuse the connection if ANY resolved
    /// address is not publicly routable, then connect only to the validated addresses. The
    /// <see cref="SocketsHttpHandler"/> layers TLS on top of the returned stream for https URLs.</summary>
    private static async ValueTask<Stream> SafeConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }

        // Validate the EXACT set we will connect to (no re-resolution between check and connect,
        // so there is no TOCTOU window here). Extracted to EnsureAddressesSafe so this SSRF
        // enforcement can be unit-tested without opening a socket (see WebhookNotifierTests).
        EnsureAddressesSafe(host, addresses);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Throws <see cref="HttpRequestException"/> unless EVERY candidate address for
    /// <paramref name="host"/> is publicly routable (per <see cref="BannerController.IsIpAddressSafeToCall"/>),
    /// and unless there is at least one address. This is the connection-time SSRF gate that actually
    /// defeats DNS rebinding and redirect-to-internal (the entry-time name check in
    /// <see cref="BannerController.IsWebhookHostSafe"/> is only fast-fail UX). Kept as an internal
    /// static — separate from the socket I/O in <see cref="SafeConnectAsync"/> — so the enforcement
    /// invariant can be unit-tested without a live DNS lookup or an open socket.</summary>
    internal static void EnsureAddressesSafe(string host, IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0)
            throw new HttpRequestException($"Webhook host '{host}' did not resolve to any address.");

        foreach (var addr in addresses)
        {
            if (!BannerController.IsIpAddressSafeToCall(addr, out var reason))
                throw new HttpRequestException($"Refusing webhook connection to '{host}' ({addr}): it {reason}.");
        }
    }
}
