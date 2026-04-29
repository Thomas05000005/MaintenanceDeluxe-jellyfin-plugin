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
///   <item>Startup consistency check: logs once per process lifetime when starting mid-maintenance.</item>
///   <item>Periodic drift check: while maintenance is active, re-disables any tracked user that
///         another admin re-enabled via the Jellyfin dashboard. Prevents the maintenance promise
///         from being silently broken between two server restarts.</item>
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
        // Single Plugin.Instance read for the whole tick. The plugin singleton is
        // initialised once at process startup and lives until shutdown, so this
        // reference cannot become null partway through. plugin.Configuration is
        // an object reference whose fields the helpers mutate in place, so we
        // re-read plugin.Configuration.MaintenanceMode at each branch boundary
        // to see the post-helper state without re-fetching the singleton.
        var plugin = Plugin.Instance;
        if (plugin is null) return;

        var now = DateTime.UtcNow;

        // ── Startup log (once per process lifetime) ────────────────────────────────
        // The actual re-disable now happens unconditionally in the periodic drift
        // check below — this branch only exists to surface a clear log line on first
        // tick after a mid-maintenance restart.
        if (!_startupCheckDone)
        {
            _startupCheckDone = true;
            if (plugin.Configuration.MaintenanceMode.IsActive)
                _logger.LogInformation("[MaintenanceDeluxe] Server started mid-maintenance — periodic drift check will re-disable any user re-enabled during the restart.");
        }

        // ── Periodic drift check ───────────────────────────────────────────────────
        // Runs every tick (1 min). If another admin re-enables a user via the
        // Jellyfin dashboard while maintenance is active, the next tick puts them
        // back in the disabled state.
        if (plugin.Configuration.MaintenanceMode.IsActive)
            await MaintenanceHelper.EnsureUsersDisabledAsync(_userManager, _logger).ConfigureAwait(false);

        // ── Schedule: auto-activate ────────────────────────────────────────────────
        var maint = plugin.Configuration.MaintenanceMode;
        if (maint.ScheduleEnabled
            && maint.ScheduledStart.HasValue
            && now >= maint.ScheduledStart.Value
            && !maint.IsActive)
        {
            _logger.LogInformation("[MaintenanceDeluxe] Scheduled maintenance activation triggered at {Time}.", now);
            await MaintenanceHelper.ActivateAsync(_userManager, _logger).ConfigureAwait(false);
            var freshMaint = plugin.Configuration.MaintenanceMode;
            await WebhookNotifier.NotifyAsync(freshMaint.Webhook, WebhookEvent.Activated, freshMaint, _httpFactory, _logger, cancellationToken).ConfigureAwait(false);
        }

        // ── Schedule: auto-deactivate ──────────────────────────────────────────────
        maint = plugin.Configuration.MaintenanceMode;
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
            var config = plugin.Configuration;
            var endValue = config.MaintenanceMode.ScheduledEnd;
            var restartValue = config.MaintenanceMode.ScheduledRestart;
            config.MaintenanceMode.ScheduleEnabled = false;
            config.MaintenanceMode.ScheduledStart = null;
            config.MaintenanceMode.ScheduledEnd = null;
            if (restartValue.HasValue && endValue.HasValue && restartValue.Value <= endValue.Value)
                config.MaintenanceMode.ScheduledRestart = null;
            plugin.UpdateConfiguration(config);
            plugin.SaveConfiguration();
        }

        // ── Scheduled restart ──────────────────────────────────────────────────────
        maint = plugin.Configuration.MaintenanceMode;
        if (maint.ScheduledRestart.HasValue && now >= maint.ScheduledRestart.Value)
        {
            _logger.LogInformation("[MaintenanceDeluxe] Scheduled server restart triggered at {Time}.", now);

            // Notify webhook BEFORE restart so subscribers know what's happening.
            // We await it (with WebhookNotifier's own 5s timeout cap) so the HTTP request
            // gets a chance to complete before Jellyfin tears down the host. The notifier
            // never throws, so a webhook failure cannot prevent the restart.
            await WebhookNotifier.NotifyAsync(maint.Webhook, WebhookEvent.Restarting, maint, _httpFactory, _logger, cancellationToken).ConfigureAwait(false);

            var config = plugin.Configuration;
            config.MaintenanceMode.ScheduledRestart = null;
            plugin.UpdateConfiguration(config);
            plugin.SaveConfiguration();
            _systemManager.Restart();
        }

        progress.Report(100);
    }
}
