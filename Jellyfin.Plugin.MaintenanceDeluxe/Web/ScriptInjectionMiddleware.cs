using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MaintenanceDeluxe.Web;

/// <summary>
/// Injects the suite's client script tag into the Jellyfin web UI by rewriting the
/// <c>index.html</c> RESPONSE BODY in flight — the file on disk is never touched.
///
/// Why this exists: every other approach in the ecosystem either patches jellyfin-web on disk
/// (which fails on Docker installs where the plugin has no write permission on the web root) or
/// depends on the third-party JavaScript Injector + File Transformation pair. That pair is a hard
/// dependency we want to shed: as of 2026-08 JavaScript Injector still has no Jellyfin 12 build,
/// so on a 12.x server the plugin would be a silent no-op. Response rewriting needs no filesystem
/// access, no third-party plugin, and survives server upgrades because nothing is persisted.
///
/// The middleware is deliberately conservative: it only buffers a response when the request looks
/// like the web UI document itself, and any failure falls through to the original bytes.
/// </summary>
public sealed class ScriptInjectionMiddleware
{
    /// <summary>Marker attribute so a page that somehow gets injected twice is detectable, and so
    /// the client can find its own tag.</summary>
    internal const string ScriptElementId = "maintenancedeluxe-suite";

    private readonly RequestDelegate _next;
    private readonly ILogger<ScriptInjectionMiddleware> _logger;

    /// <summary>Initializes a new instance of the <see cref="ScriptInjectionMiddleware"/> class.</summary>
    /// <param name="next">Next middleware in the pipeline.</param>
    /// <param name="logger">Logger.</param>
    public ScriptInjectionMiddleware(RequestDelegate next, ILogger<ScriptInjectionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>Builds the tag injected before <c>&lt;/body&gt;</c>. The script is served by our own
    /// controller and versioned by the assembly version so a plugin upgrade busts the browser cache
    /// (the endpoint itself answers with an assembly-version ETag).</summary>
    internal static string BuildScriptTag(string version) =>
        $"<script id=\"{ScriptElementId}\" src=\"/MaintenanceDeluxe/banner.js?v={version}\" defer></script>";

    /// <summary>Injects <paramref name="tag"/> into <paramref name="html"/> before the closing
    /// body tag (falling back to the closing html tag, then to append). Returns the original string
    /// unchanged when the tag is already present, so a double injection path (e.g. this middleware
    /// AND a legacy JS Injector registration) cannot load the script twice.</summary>
    internal static string InjectIntoHtml(string html, string tag)
    {
        if (string.IsNullOrEmpty(html)) return html;
        if (html.Contains(ScriptElementId, StringComparison.Ordinal)) return html;

        var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = html.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? html + tag : html.Insert(idx, tag);
    }

    /// <summary>True when the request targets the web UI document (the SPA shell) rather than an
    /// asset or an API call. Jellyfin serves the shell at "/", "/web", "/web/" and
    /// "/web/index.html"; everything else (/System/..., /Items/..., *.js, *.css) is skipped so we
    /// never buffer a media stream or an API payload.</summary>
    internal static bool IsWebUiDocumentPath(PathString path)
    {
        if (!path.HasValue) return true; // "/" arrives as an empty PathString
        var p = path.Value!;
        if (p.Length == 0 || p == "/") return true;
        return p.Equals("/web", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/web/", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Processes a request.</summary>
    /// <param name="context">HTTP context.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!HttpMethods.IsGet(context.Request.Method) || !IsWebUiDocumentPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);

            buffer.Seek(0, SeekOrigin.Begin);
            var contentType = context.Response.ContentType;
            var isHtml = contentType is not null
                && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase);

            // Only rewrite a successful HTML document; anything else (304, redirect, JSON, an
            // asset that happened to match the path test) is streamed back byte-for-byte.
            if (!isHtml || context.Response.StatusCode != StatusCodes.Status200OK)
            {
                context.Response.Body = originalBody;
                await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
                return;
            }

            var html = Encoding.UTF8.GetString(buffer.ToArray());
            var version = Plugin.Instance?.Version?.ToString() ?? "0";
            var injected = InjectIntoHtml(html, BuildScriptTag(version));

            context.Response.Body = originalBody;
            var bytes = Encoding.UTF8.GetBytes(injected);
            // The body grew: a stale Content-Length would truncate the document in the browser.
            context.Response.ContentLength = bytes.Length;
            await originalBody.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never break the web UI because of us: fall back to the unmodified response.
            _logger.LogError(ex, "Script injection failed; serving the original response.");
            context.Response.Body = originalBody;
            if (buffer.Length > 0 && !context.Response.HasStarted)
            {
                buffer.Seek(0, SeekOrigin.Begin);
                await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            }
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }
}
