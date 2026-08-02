using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.MaintenanceDeluxe.Web;

/// <summary>
/// Hooks <see cref="ScriptInjectionMiddleware"/> into Jellyfin's ASP.NET pipeline.
///
/// <para>ASP.NET Core resolves every <see cref="IStartupFilter"/> registered in the service
/// collection when the host is built and runs them around the application's own Configure step.
/// A Jellyfin plugin can therefore register one from its <c>IPluginServiceRegistrator</c> and get
/// middleware into the server pipeline without the server knowing anything about us — this is what
/// makes standalone script delivery (no JavaScript Injector, no File Transformation) possible.</para>
///
/// <para>Our middleware is added FIRST (before the server's own Configure runs) so it wraps the
/// static-file middleware that ultimately serves index.html: the inner pipeline produces the
/// document, and our wrapper rewrites it on the way out.</para>
/// </summary>
public sealed class InjectionStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            app.UseMiddleware<ScriptInjectionMiddleware>();
            next(app);
        };
    }
}
