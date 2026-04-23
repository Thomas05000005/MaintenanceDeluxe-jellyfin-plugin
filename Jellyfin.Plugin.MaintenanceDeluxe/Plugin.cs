using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Jellyfin.Plugin.MaintenanceDeluxe.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.MaintenanceDeluxe;

/// <summary>
/// Main plugin entry point for MaintenanceDeluxe.
/// Registers the admin config page and injects the banner script via JS Injector (if present).
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<Plugin> _logger;
    private int _retryCount = 0;
    private const int MaxRetries = 3;

    /// <summary>
    /// Gets the singleton plugin instance.
    /// Set during single-threaded DI startup — no lock required.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Initializes a new instance of <see cref="Plugin"/>.</summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _logger = logger;
        Instance = this;
        RegisterWithJsInjector();
    }

    /// <inheritdoc />
    public override string Name => "MaintenanceDeluxe";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("c462a69c-b428-4ecf-b5f2-a28cae71b0fe");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "campaign"
        };
    }

    private void RegisterWithJsInjector()
    {
        try
        {
            var jsInjectorAssembly = AssemblyLoadContext.All
                .SelectMany(ctx => ctx.Assemblies)
                .FirstOrDefault(a => a.FullName?.Contains("Jellyfin.Plugin.JavaScriptInjector") ?? false);

            if (jsInjectorAssembly is null)
            {
                _logger.LogInformation("[MaintenanceDeluxe] JS Injector plugin not found — banner script will not be injected.");
                return;
            }

            var pluginInterfaceType = jsInjectorAssembly.GetType("Jellyfin.Plugin.JavaScriptInjector.PluginInterface");
            if (pluginInterfaceType is null)
            {
                _logger.LogWarning("[MaintenanceDeluxe] JS Injector PluginInterface type not found.");
                return;
            }

            var resourceName = "Jellyfin.Plugin.MaintenanceDeluxe.Resources.banner.js";
            string scriptContent;
            using (var stream = GetType().Assembly.GetManifestResourceStream(resourceName))
            {
                if (stream is null)
                {
                    _logger.LogWarning("[MaintenanceDeluxe] Embedded resource '{Resource}' not found.", resourceName);
                    return;
                }

                using var reader = new StreamReader(stream);
                scriptContent = reader.ReadToEnd();
            }

            var payload = new JObject
            {
                { "id",                     $"{Id}-banner-script" },
                { "name",                   "MaintenanceDeluxe Script" },
                { "script",                 scriptContent },
                { "enabled",                true },
                // false: the maintenance overlay MUST be able to show on the
                // unauthenticated login page so kicked/disabled users see the
                // explanation instead of just "account disabled, contact admin".
                // The /MaintenanceDeluxe/maintenance endpoint is explicitly public
                // to support this (see BannerController.GetMaintenance).
                { "requiresAuthentication", false },
                { "pluginId",               Id.ToString() },
                { "pluginName",             Name },
                { "pluginVersion",          Version?.ToString() ?? "1.0.0" }
            };

            pluginInterfaceType.GetMethod("RegisterScript")?.Invoke(null, new object?[] { payload });
            _logger.LogInformation("[MaintenanceDeluxe] Banner script registered with JS Injector.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
        {
            // JS Injector's singleton is not ready yet — retry after all plugins have initialised.
            if (_retryCount >= MaxRetries)
            {
                _logger.LogWarning("[MaintenanceDeluxe] JS Injector not ready after {MaxRetries} retries — banner script will not be injected.", MaxRetries);
                return;
            }

            _retryCount++;
            _logger.LogInformation("[MaintenanceDeluxe] JS Injector not ready yet, retrying in 5 s (attempt {Attempt}/{MaxRetries})...", _retryCount, MaxRetries);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                RegisterWithJsInjector();
            });
        }
        catch (Exception ex)
        {
            // Intentionally broad: this plugin must not crash the server.
            _logger.LogError(ex, "[MaintenanceDeluxe] Failed to register with JS Injector.");
        }
    }
}
