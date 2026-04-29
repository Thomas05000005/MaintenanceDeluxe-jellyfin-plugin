using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MaintenanceDeluxe;

/// <summary>
/// Server-side helpers for activating and deactivating maintenance mode.
/// Called by both <see cref="Api.BannerController"/> and
/// <see cref="ScheduledTasks.MaintenanceScheduleTask"/> to avoid duplicating logic.
/// A SemaphoreSlim serialises concurrent calls so the controller and the scheduler
/// can never race on the IsActive guard.
/// </summary>
internal static class MaintenanceHelper
{
    // Serialises concurrent calls from the HTTP controller and the 1-min scheduled task.
    private static readonly SemaphoreSlim _mutex = new(1, 1);

    // ── Pure decision helpers (extracted for unit-testability) ─────────────────
    // These do no I/O and are exposed as `internal` so the test project can verify
    // the partitioning logic without instantiating the Plugin singleton or mocking
    // SaveConfiguration.

    /// <summary>True if the user has the IsAdministrator permission set to true.</summary>
    internal static bool IsAdmin(User u) =>
        u.Permissions.Any(p => p.Kind == PermissionKind.IsAdministrator && p.Value);

    /// <summary>True if the user has the IsDisabled permission set to true.</summary>
    internal static bool IsDisabled(User u) =>
        u.Permissions.Any(p => p.Kind == PermissionKind.IsDisabled && p.Value);

    /// <summary>Returns the non-admin users that are currently enabled and NOT in the whitelist —
    /// i.e. the users that activation should disable. Pure function: no I/O, no side effects.</summary>
    internal static List<User> SelectUsersToDisable(IEnumerable<User> users, IReadOnlyCollection<string> whitelist)
    {
        return users
            .Where(u => !IsAdmin(u))
            .Where(u => !IsDisabled(u))
            .Where(u => !whitelist.Contains(u.Id.ToString()))
            .ToList();
    }

    /// <summary>Returns the IDs of non-admin users that are ALREADY disabled at activation time —
    /// i.e. the users that deactivation must NOT re-enable (preserves admin's prior intent).
    /// Pure function: no I/O, no side effects.</summary>
    internal static List<string> SelectPreDisabledIds(IEnumerable<User> users)
    {
        return users
            .Where(u => !IsAdmin(u))
            .Where(IsDisabled)
            .Select(u => u.Id.ToString())
            .ToList();
    }

    /// <summary>
    /// Disables all non-admin, non-already-disabled users and marks maintenance as active.
    /// No-op if maintenance is already active.
    /// Only users successfully disabled are recorded in <c>maintenanceDisabledUserIds</c>;
    /// a per-user failure is logged and skipped rather than aborting the whole activation.
    /// </summary>
    internal static async Task ActivateAsync(IUserManager userManager, ILogger? logger = null)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            var plugin = Plugin.Instance;
            if (plugin is null) return;

            var config = plugin.Configuration;
            var maint = config.MaintenanceMode;
            if (maint.IsActive) return;

            var allUsers = userManager.Users.ToList();
            var preDisabled = SelectPreDisabledIds(allUsers);
            var whitelist = maint.WhitelistedUserIds ?? new List<string>();
            var toDisable = SelectUsersToDisable(allUsers, whitelist);

            var successfullyDisabled = new List<string>();
            foreach (var user in toDisable)
            {
                try
                {
                    var dto = userManager.GetUserDto(user, string.Empty);
                    var policy = dto.Policy ?? new UserPolicy();
                    policy.IsDisabled = true;
                    await userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
                    successfullyDisabled.Add(user.Id.ToString());
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "[MaintenanceDeluxe] Failed to disable user {UserId} during maintenance activation — skipping.", user.Id);
                }
            }

            maint.IsActive = true;
            maint.ActivatedAt = DateTime.UtcNow;
            maint.PreDisabledUserIds = preDisabled;
            maint.MaintenanceDisabledUserIds = successfullyDisabled;
            plugin.UpdateConfiguration(config);
            plugin.SaveConfiguration();

            logger?.LogInformation("[MaintenanceDeluxe] Maintenance activated. Disabled {Count} user(s); {Skipped} skipped.",
                successfullyDisabled.Count, toDisable.Count - successfullyDisabled.Count);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Re-enables the users that MaintenanceDeluxe disabled on activation and marks maintenance as inactive.
    /// No-op if maintenance is not active.
    /// Per-user failures are logged and skipped so a single bad record does not block the rest.
    /// </summary>
    internal static async Task DeactivateAsync(IUserManager userManager, ILogger? logger = null)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            var plugin = Plugin.Instance;
            if (plugin is null) return;

            var config = plugin.Configuration;
            var maint = config.MaintenanceMode;
            if (!maint.IsActive) return;

            int reenabled = 0;
            foreach (var idStr in maint.MaintenanceDisabledUserIds)
            {
                if (!Guid.TryParse(idStr, out var guid))
                {
                    logger?.LogWarning("[MaintenanceDeluxe] Skipping malformed user ID '{Id}' during deactivation.", idStr);
                    continue;
                }

                var user = userManager.GetUserById(guid);
                if (user is null)
                {
                    logger?.LogDebug("[MaintenanceDeluxe] User {UserId} not found during deactivation (may have been deleted) — skipping.", guid);
                    continue;
                }

                try
                {
                    var dto = userManager.GetUserDto(user, string.Empty);
                    var policy = dto.Policy ?? new UserPolicy();
                    policy.IsDisabled = false;
                    await userManager.UpdatePolicyAsync(guid, policy).ConfigureAwait(false);
                    reenabled++;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "[MaintenanceDeluxe] Failed to re-enable user {UserId} during deactivation — skipping.", guid);
                }
            }

            maint.IsActive = false;
            maint.ActivatedAt = null;
            maint.PreDisabledUserIds = [];
            maint.MaintenanceDisabledUserIds = [];
            plugin.UpdateConfiguration(config);
            plugin.SaveConfiguration();

            logger?.LogInformation("[MaintenanceDeluxe] Maintenance deactivated. Re-enabled {Count} user(s).", reenabled);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Startup consistency check: re-disables any tracked user that is no longer disabled.
    /// Called once per process lifetime to handle server restarts during active maintenance.
    /// No-op if maintenance is not active.
    /// </summary>
    internal static async Task EnsureUsersDisabledAsync(IUserManager userManager, ILogger? logger = null)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            var plugin = Plugin.Instance;
            if (plugin is null) return;

            var maint = plugin.Configuration.MaintenanceMode;
            if (!maint.IsActive) return;

            int restored = 0;
            var restoredNames = new List<string>();
            foreach (var idStr in maint.MaintenanceDisabledUserIds)
            {
                if (!Guid.TryParse(idStr, out var guid)) continue;
                var user = userManager.GetUserById(guid);
                if (user is null) continue;

                var isCurrentlyDisabled = user.Permissions.Any(p => p.Kind == PermissionKind.IsDisabled && p.Value);
                if (!isCurrentlyDisabled)
                {
                    try
                    {
                        var dto = userManager.GetUserDto(user, string.Empty);
                        var policy = dto.Policy ?? new UserPolicy();
                        policy.IsDisabled = true;
                        await userManager.UpdatePolicyAsync(guid, policy).ConfigureAwait(false);
                        restored++;
                        restoredNames.Add(user.Username ?? guid.ToString());
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "[MaintenanceDeluxe] Failed to re-disable user {UserName} ({UserId}) during drift check.", user.Username ?? "?", guid);
                    }
                }
            }

            if (restored > 0)
                logger?.LogInformation("[MaintenanceDeluxe] Drift check re-disabled {Count} user(s) that were re-enabled mid-maintenance: {Users}.", restored, string.Join(", ", restoredNames));
        }
        finally
        {
            _mutex.Release();
        }
    }
}
