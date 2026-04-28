using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MaintenanceDeluxe.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MaintenanceDeluxe.ScheduledTasks;

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
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MaintenanceScheduleTask> _logger;

    // One-time flag per process lifetime — reset on restart, which is the intended behaviour.
    private static bool _startupCheckDone;

    /// <summary>Initializes a new instance of <see cref="MaintenanceScheduleTask"/>.</summary>
    public MaintenanceScheduleTask(
        IUserManager userManager,
        ISystemManager systemManager,
        IHttpClientFactory httpFactory,
        ILogger<MaintenanceScheduleTask> logger)
    {
        _userManager = userManager;
        _systemManager = systemManager;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "MaintenanceDeluxe Maintenance Schedule";

    /// <inheritdoc />
    public string Description => "Activates/deactivates maintenance mode on schedule and triggers scheduled server restarts.";

    /// <inheritdoc />
    public string Category => "MaintenanceDeluxe";

    /// <inheritdoc />
    public string Key => "MaintenanceDeluxeMaintenanceSchedule";

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
                _logger.LogInformation("[MaintenanceDeluxe] Server started mid-maintenance — re-verifying user disabled state.");
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
            _logger.LogInformation("[MaintenanceDeluxe] Scheduled maintenance activation triggered at {Time}.", now);
            await MaintenanceHelper.ActivateAsync(_userManager, _logger).ConfigureAwait(false);
            var freshMaint = Plugin.Instance?.Configuration.MaintenanceMode;
            if (freshMaint is not null)
                await WebhookNotifier.NotifyAsync(freshMaint.Webhook, WebhookEvent.Activated, freshMaint, _httpFactory, _logger, cancellationToken).ConfigureAwait(false);
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
            _logger.LogInformation("[MaintenanceDeluxe] Scheduled maintenance deactivation triggered at {Time}.", now);
            // Snapshot counts before DeactivateAsync clears the lists.
            var snapshot = new MaintenanceSetting
            {
                IsActive = maint.IsActive,
                Message = maint.Message,
                StatusUrl = maint.StatusUrl,
                CustomTitle = maint.CustomTitle,
                CustomSubtitle = maint.CustomSubtitle,
                ScheduledRestart = maint.ScheduledRestart,
                MaintenanceDisabledUserIds = new List<string>(maint.MaintenanceDisabledUserIds),
                WhitelistedUserIds = new List<string>(maint.WhitelistedUserIds)
            };
            var hookSettings = maint.Webhook;
            await MaintenanceHelper.DeactivateAsync(_userManager, _logger).ConfigureAwait(false);
            await WebhookNotifier.NotifyAsync(hookSettings, WebhookEvent.Deactivated, snapshot, _httpFactory, _logger, cancellationToken).ConfigureAwait(false);

            // Clear the schedule so the activation check doesn't immediately re-trigger.
            // Also clear ScheduledRestart if it was inside the window (admin's intent was likely
            // "restart as part of this maintenance"); preserve it if it was set after ScheduledEnd
            // (admin's intent was likely "schedule a restart later, independent of this window").
            plugin = Plugin.Instance;
            if (plugin is not null)
            {
                var config = plugin.Configuration;
                var endValue = config.MaintenanceMode.ScheduledEnd;
                var restartValue = config.MaintenanceMode.ScheduledRestart;
                config.MaintenanceMode.ScheduleEnabled = false;
                config.MaintenanceMode.ScheduledStart = null;
                config.MaintenanceMode.ScheduledEnd = null;
                if (restartValue.HasValue && endValue.HasValue && restartValue.Value <= endValue.Value)
                {
                    config.MaintenanceMode.ScheduledRestart = null;
                }
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
            _logger.LogInformation("[MaintenanceDeluxe] Scheduled server restart triggered at {Time}.", now);
            var config = plugin.Configuration;
            config.MaintenanceMode.ScheduledRestart = null;
            plugin.UpdateConfiguration(config);
            plugin.SaveConfiguration();
            _systemManager.Restart();
        }

        progress.Report(100);
    }
}
