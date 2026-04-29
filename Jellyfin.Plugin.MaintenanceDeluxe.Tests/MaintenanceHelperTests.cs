using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Xunit;

namespace Jellyfin.Plugin.MaintenanceDeluxe.Tests;

/// <summary>
/// Integration tests for the pure decision helpers in <see cref="MaintenanceHelper"/>.
/// We don't test ActivateAsync / DeactivateAsync end-to-end because they bind to the
/// Plugin singleton (Plugin.Instance.UpdateConfiguration / SaveConfiguration), but we
/// DO test the partitioning logic that decides who gets disabled — that's where the
/// business rules live and where regressions would silently break the maintenance
/// promise.
/// </summary>
public class MaintenanceHelperTests
{
    private const string AuthProvider = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";
    private const string PasswordResetProvider = "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider";

    private static User MakeUser(string username, bool isAdmin = false, bool isDisabled = false)
    {
        var u = new User(username, AuthProvider, PasswordResetProvider);
        if (isAdmin)
            u.Permissions.Add(new Permission(PermissionKind.IsAdministrator, true));
        if (isDisabled)
            u.Permissions.Add(new Permission(PermissionKind.IsDisabled, true));
        return u;
    }

    [Fact]
    public void SelectUsersToDisable_PicksOnlyEnabledNonAdminsNotInWhitelist()
    {
        var admin = MakeUser("admin", isAdmin: true);
        var alice = MakeUser("alice");                          // should be disabled
        var bob = MakeUser("bob", isDisabled: true);            // already disabled — skip
        var carol = MakeUser("carol");                          // whitelisted — skip
        var dave = MakeUser("dave");                            // should be disabled
        var users = new[] { admin, alice, bob, carol, dave };
        var whitelist = new List<string> { carol.Id.ToString() };

        var toDisable = MaintenanceHelper.SelectUsersToDisable(users, whitelist);

        Assert.Equal(2, toDisable.Count);
        Assert.Contains(toDisable, u => u.Username == "alice");
        Assert.Contains(toDisable, u => u.Username == "dave");
        Assert.DoesNotContain(toDisable, u => u.Username == "admin");
        Assert.DoesNotContain(toDisable, u => u.Username == "bob");
        Assert.DoesNotContain(toDisable, u => u.Username == "carol");
    }

    [Fact]
    public void SelectUsersToDisable_EmptyWhitelist_DisablesAllEnabledNonAdmins()
    {
        var users = new[]
        {
            MakeUser("admin", isAdmin: true),
            MakeUser("u1"),
            MakeUser("u2"),
            MakeUser("u3", isDisabled: true)
        };

        var toDisable = MaintenanceHelper.SelectUsersToDisable(users, Array.Empty<string>());

        Assert.Equal(2, toDisable.Count);
        Assert.All(toDisable, u => Assert.Contains(u.Username, new[] { "u1", "u2" }));
    }

    [Fact]
    public void SelectUsersToDisable_AllUsersAreAdmins_ReturnsEmpty()
    {
        var users = new[]
        {
            MakeUser("admin1", isAdmin: true),
            MakeUser("admin2", isAdmin: true)
        };

        var toDisable = MaintenanceHelper.SelectUsersToDisable(users, Array.Empty<string>());

        Assert.Empty(toDisable);
    }

    [Fact]
    public void SelectUsersToDisable_NoUsers_ReturnsEmpty()
    {
        var toDisable = MaintenanceHelper.SelectUsersToDisable(Array.Empty<User>(), Array.Empty<string>());
        Assert.Empty(toDisable);
    }

    [Fact]
    public void SelectPreDisabledIds_OnlyReturnsAlreadyDisabledNonAdmins()
    {
        var admin = MakeUser("admin", isAdmin: true);
        var disabledAdmin = MakeUser("disabledAdmin", isAdmin: true, isDisabled: true);
        var alice = MakeUser("alice");                        // enabled — not pre-disabled
        var bob = MakeUser("bob", isDisabled: true);          // pre-disabled non-admin
        var carol = MakeUser("carol", isDisabled: true);      // pre-disabled non-admin
        var users = new[] { admin, disabledAdmin, alice, bob, carol };

        var preDisabled = MaintenanceHelper.SelectPreDisabledIds(users);

        Assert.Equal(2, preDisabled.Count);
        Assert.Contains(bob.Id.ToString(), preDisabled);
        Assert.Contains(carol.Id.ToString(), preDisabled);
        // Admins (even disabled) are NOT in the pre-disabled list — they're not subject
        // to maintenance disabling at all.
        Assert.DoesNotContain(disabledAdmin.Id.ToString(), preDisabled);
    }

    [Fact]
    public void SelectPreDisabledIds_AndSelectUsersToDisable_AreDisjoint()
    {
        // Critical invariant: a single user cannot end up in both lists, otherwise
        // DeactivateAsync would re-enable a user it never disabled (preDisabled list
        // is meant to be skipped on re-enable).
        var users = new[]
        {
            MakeUser("admin", isAdmin: true),
            MakeUser("enabled-1"),
            MakeUser("enabled-2"),
            MakeUser("preDisabled-1", isDisabled: true),
            MakeUser("preDisabled-2", isDisabled: true)
        };

        var toDisable = MaintenanceHelper.SelectUsersToDisable(users, Array.Empty<string>());
        var preDisabled = MaintenanceHelper.SelectPreDisabledIds(users);

        var toDisableIds = toDisable.Select(u => u.Id.ToString()).ToHashSet();
        var preDisabledIds = preDisabled.ToHashSet();
        Assert.Empty(toDisableIds.Intersect(preDisabledIds));
    }

    [Fact]
    public void IsAdmin_RecognisesAdministratorPermission()
    {
        Assert.True(MaintenanceHelper.IsAdmin(MakeUser("a", isAdmin: true)));
        Assert.False(MaintenanceHelper.IsAdmin(MakeUser("b", isAdmin: false)));
        // Disabled admin is still an admin (the IsDisabled flag is independent).
        Assert.True(MaintenanceHelper.IsAdmin(MakeUser("c", isAdmin: true, isDisabled: true)));
    }

    [Fact]
    public void IsDisabled_RecognisesDisabledPermission()
    {
        Assert.False(MaintenanceHelper.IsDisabled(MakeUser("a")));
        Assert.True(MaintenanceHelper.IsDisabled(MakeUser("b", isDisabled: true)));
        Assert.True(MaintenanceHelper.IsDisabled(MakeUser("c", isAdmin: true, isDisabled: true)));
    }

    [Fact]
    public void SelectUsersToDisable_WhitelistTakesPrecedenceOverEnabledStatus()
    {
        var carol = MakeUser("carol");
        var users = new[] { carol };

        var withWhitelist = MaintenanceHelper.SelectUsersToDisable(users, new[] { carol.Id.ToString() });
        var withoutWhitelist = MaintenanceHelper.SelectUsersToDisable(users, Array.Empty<string>());

        Assert.Empty(withWhitelist);
        Assert.Single(withoutWhitelist);
    }

    // ─── PartitionDeactivationTargets — covers DeactivateAsync's input handling ───

    [Fact]
    public void PartitionDeactivationTargets_ClassifiesIntoThreeBuckets()
    {
        var alice = MakeUser("alice", isDisabled: true);
        var bob = MakeUser("bob", isDisabled: true);
        var ghost = Guid.NewGuid(); // valid GUID, no matching user
        var ids = new[]
        {
            alice.Id.ToString(),
            "not-a-guid",
            bob.Id.ToString(),
            ghost.ToString(),
            "",
            "00000000-0000-0000-0000"   // malformed
        };
        var lookup = (Guid g) =>
            g == alice.Id ? alice :
            g == bob.Id   ? bob   : null;

        var plan = MaintenanceHelper.PartitionDeactivationTargets(ids, lookup);

        Assert.Equal(2, plan.ToReEnable.Count);
        Assert.Contains(plan.ToReEnable, t => t.User.Username == "alice");
        Assert.Contains(plan.ToReEnable, t => t.User.Username == "bob");

        Assert.Equal(3, plan.MalformedIds.Count); // "not-a-guid" + "" + "00000000-0000-0000-0000"
        Assert.Contains("not-a-guid", plan.MalformedIds);
        Assert.Contains("", plan.MalformedIds);

        Assert.Single(plan.MissingUserIds);
        Assert.Equal(ghost, plan.MissingUserIds[0]);
    }

    [Fact]
    public void PartitionDeactivationTargets_EmptyInput_ReturnsEmptyPlan()
    {
        var plan = MaintenanceHelper.PartitionDeactivationTargets(Array.Empty<string>(), _ => null);
        Assert.Empty(plan.ToReEnable);
        Assert.Empty(plan.MalformedIds);
        Assert.Empty(plan.MissingUserIds);
    }

    [Fact]
    public void PartitionDeactivationTargets_AllValidUsers_NoDataLoss()
    {
        var users = Enumerable.Range(0, 10).Select(i => MakeUser($"u{i}")).ToList();
        var byId = users.ToDictionary(u => u.Id, u => u);

        var plan = MaintenanceHelper.PartitionDeactivationTargets(
            users.Select(u => u.Id.ToString()),
            id => byId.TryGetValue(id, out var u) ? u : null);

        Assert.Equal(10, plan.ToReEnable.Count);
        Assert.Empty(plan.MalformedIds);
        Assert.Empty(plan.MissingUserIds);
    }

    // ─── SelectUsersNeedingReDisable — covers EnsureUsersDisabledAsync's drift check ───

    [Fact]
    public void SelectUsersNeedingReDisable_OnlyReturnsCurrentlyEnabledUsers()
    {
        // alice still disabled → no action needed
        // bob got re-enabled by another admin → MUST be in the result
        // carol got re-enabled too → MUST be in the result
        var alice = MakeUser("alice", isDisabled: true);
        var bob = MakeUser("bob", isDisabled: false);
        var carol = MakeUser("carol", isDisabled: false);
        var users = new[] { alice, bob, carol };
        var byId = users.ToDictionary(u => u.Id, u => u);

        var drifted = MaintenanceHelper.SelectUsersNeedingReDisable(
            users.Select(u => u.Id.ToString()),
            id => byId.TryGetValue(id, out var u) ? u : null);

        Assert.Equal(2, drifted.Count);
        Assert.Contains(drifted, t => t.User.Username == "bob");
        Assert.Contains(drifted, t => t.User.Username == "carol");
        Assert.DoesNotContain(drifted, t => t.User.Username == "alice");
    }

    [Fact]
    public void SelectUsersNeedingReDisable_SilentlySkipsMalformedAndMissing()
    {
        // Drift check shouldn't pollute logs with malformed/deleted entries — those are
        // routine artefacts, the drift check just cares about live drift to fix.
        var alice = MakeUser("alice", isDisabled: false);
        var ids = new[]
        {
            alice.Id.ToString(),
            "garbage",
            Guid.NewGuid().ToString(), // deleted
            ""
        };
        var lookup = (Guid g) => g == alice.Id ? alice : null;

        var drifted = MaintenanceHelper.SelectUsersNeedingReDisable(ids, lookup);

        Assert.Single(drifted);
        Assert.Equal("alice", drifted[0].User.Username);
    }

    [Fact]
    public void SelectUsersNeedingReDisable_NoDriftedUsers_ReturnsEmpty()
    {
        // The happy path: maintenance is healthy, all tracked users still disabled.
        // Callers can use this to skip the per-user logging entirely.
        var users = Enumerable.Range(0, 5).Select(i => MakeUser($"u{i}", isDisabled: true)).ToList();
        var byId = users.ToDictionary(u => u.Id, u => u);

        var drifted = MaintenanceHelper.SelectUsersNeedingReDisable(
            users.Select(u => u.Id.ToString()),
            id => byId.TryGetValue(id, out var u) ? u : null);

        Assert.Empty(drifted);
    }
}
