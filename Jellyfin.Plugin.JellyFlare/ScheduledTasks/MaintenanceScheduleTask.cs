using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyFlare.ScheduledTasks;

/// <summary>
/// Runs every minute to drive maintenance scheduling, persistence hardening, and scheduled server restart.
/// <list type="bullet">
///   <item>Startup consistency check: re-disables tracked users if the server restarted mid-maintenance.</item>
///   <item>Schedule activate/deactivate: triggers based on <see cref="Configuration.MaintenanceSetting.ScheduledStart"/> / <see cref="Configuration.MaintenanceSetting.ScheduledEnd"/>.</item>
///   <item>Scheduled restart: restarts the server at <see cref="Configuration.MaintenanceSetting.ScheduledRestart"/>; clears the field afterward.</item>
/// </list>
/// </summary>
public class MaintenanceScheduleTask : IScheduledTask
{
    private readonly IUserManager _userManager;
    private readonly ISystemManager _systemManager;
    private readonly ILogger<MaintenanceScheduleTask> _logger;

    // One-time flag per process lifetime — reset on restart, which is the intended behaviour.
    private static bool _startupCheckDone;

    /// <summary>Initializes a new instance of <see cref="MaintenanceScheduleTask"/>.</summary>
    public MaintenanceScheduleTask(
        IUserManager userManager,
        ISystemManager systemManager,
        ILogger<MaintenanceScheduleTask> logger)
    {
        _userManager = userManager;
        _systemManager = systemManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "JellyFlare Maintenance Schedule";

    /// <inheritdoc />
    public string Description => "Activates/deactivates maintenance mode on schedule and triggers scheduled server restarts.";

    /// <inheritdoc />
    public string Category => "JellyFlare";

    /// <inheritdoc />
    public string Key => "JellyFlareMaintenanceSchedule";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(1).Ticks
        }
    ];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // ── Startup consistency check (once per process lifetime) ──────────────────
        if (!_startupCheckDone)
        {
            _startupCheckDone = true;
            var startupPlugin = Plugin.Instance;
            if (startupPlugin?.Configuration.MaintenanceMode.IsActive == true)
            {
                _logger.LogInformation("[JellyFlare] Server started mid-maintenance — re-verifying user disabled state.");
                await MaintenanceHelper.EnsureUsersDisabledAsync(_userManager, _logger).ConfigureAwait(false);
            }
        }

        // ── Schedule: auto-activate ────────────────────────────────────────────────
        var plugin = Plugin.Instance;
        if (plugin is null) return;

        var maint = plugin.Configuration.MaintenanceMode;
        var now = DateTime.UtcNow;

        if (maint.ScheduleEnabled
            && maint.ScheduledStart.HasValue
            && now >= maint.ScheduledStart.Value
            && !maint.IsActive)
        {
            _logger.LogInformation("[JellyFlare] Scheduled maintenance activation triggered at {Time}.", now);
            await MaintenanceHelper.ActivateAsync(_userManager, _logger).ConfigureAwait(false);
        }

        // Reload after possible activation.
        plugin = Plugin.Instance;
        if (plugin is null) return;
        maint = plugin.Configuration.MaintenanceMode;

        // ── Schedule: auto-deactivate ──────────────────────────────────────────────
        if (maint.ScheduleEnabled
            && maint.ScheduledEnd.HasValue
            && now >= maint.ScheduledEnd.Value
            && maint.IsActive)
        {
            _logger.LogInformation("[JellyFlare] Scheduled maintenance deactivation triggered at {Time}.", now);
            await MaintenanceHelper.DeactivateAsync(_userManager, _logger).ConfigureAwait(false);

            // Clear the schedule so the activation check doesn't immediately re-trigger.
            plugin = Plugin.Instance;
            if (plugin is not null)
            {
                var config = plugin.Configuration;
                config.MaintenanceMode.ScheduleEnabled = false;
                config.MaintenanceMode.ScheduledStart = null;
                config.MaintenanceMode.ScheduledEnd = null;
                plugin.UpdateConfiguration(config);
                plugin.SaveConfiguration();
            }
        }

        // ── Scheduled restart ──────────────────────────────────────────────────────
        plugin = Plugin.Instance;
        if (plugin is null) return;
        maint = plugin.Configuration.MaintenanceMode;

        if (maint.ScheduledRestart.HasValue && now >= maint.ScheduledRestart.Value)
        {
            _logger.LogInformation("[JellyFlare] Scheduled server restart triggered at {Time}.", now);
            var config = plugin.Configuration;
            config.MaintenanceMode.ScheduledRestart = null;
            plugin.UpdateConfiguration(config);
            plugin.SaveConfiguration();
            _systemManager.Restart();
        }

        progress.Report(100);
    }
}
