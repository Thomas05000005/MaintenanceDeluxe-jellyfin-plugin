using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.MaintenanceDeluxe.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Jellyfin.Plugin.MaintenanceDeluxe.Tests;

/// <summary>
/// Reflection guard over <see cref="BannerController"/>'s authorization contract (v0.8.6, A#36).
/// The <c>[Authorize]</c> tiers are the plugin's only server-side access control; a dropped or
/// downgraded attribute would silently expose an admin endpoint (or lock out a public asset).
/// Nothing else in the suite exercises these attributes, so this pins every action's expected
/// tier AND fails the build if a new action ships without a deliberate classification.
/// </summary>
public class ControllerAuthTests
{
    private enum Tier
    {
        /// <summary>No <c>[Authorize]</c> — reachable unauthenticated (login overlay, iframes, assets).</summary>
        Public,

        /// <summary><c>[Authorize]</c> with no policy — any authenticated user.</summary>
        AnyAuth,

        /// <summary><c>[Authorize(Policy = "RequiresElevation")]</c> — admins only.</summary>
        Admin
    }

    // Expected authorization tier per action. A NEW endpoint must be added here with a conscious
    // choice, or EveryActionHasItsExpectedAuthorizationTier fails — you cannot ship an unclassified
    // action. A dropped/changed attribute flips ActualTier and fails the same test.
    private static readonly Dictionary<string, Tier> Expected = new(StringComparer.Ordinal)
    {
        // Public (no [Authorize]) — served to unauthenticated pages / iframes / login overlay.
        ["GetMaintenance"] = Tier.Public,
        ["GetBannerScript"] = Tier.Public,
        ["GetAdminScript"] = Tier.Public,
        ["GetAdminStylesheet"] = Tier.Public,
        ["GetFont"] = Tier.Public,
        ["GetPreviewShell"] = Tier.Public,
        // Any authenticated user.
        ["GetConfig"] = Tier.AnyAuth,
        ["GetActiveAnnouncementsForCurrentUser"] = Tier.AnyAuth,
        ["MarkAnnouncementSeen"] = Tier.AnyAuth,
        // Admin only (RequiresElevation).
        ["GetConfigAdmin"] = Tier.Admin,
        ["SaveConfig"] = Tier.Admin,
        ["SaveMaintenance"] = Tier.Admin,
        ["TestWebhook"] = Tier.Admin,
        ["GetUsersSummary"] = Tier.Admin,
        ["GetActiveSessions"] = Tier.Admin,
        ["GetAdminAnnouncements"] = Tier.Admin,
        ["SaveAdminAnnouncements"] = Tier.Admin,
        ["ResetAnnouncementSeen"] = Tier.Admin,
    };

    private static IEnumerable<MethodInfo> ActionMethods() =>
        typeof(BannerController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any());

    private static Tier ActualTier(MethodInfo m)
    {
        var authz = m.GetCustomAttribute<AuthorizeAttribute>(inherit: true);
        if (authz is null) return Tier.Public;
        return string.Equals(authz.Policy, "RequiresElevation", StringComparison.Ordinal)
            ? Tier.Admin
            : Tier.AnyAuth;
    }

    [Fact]
    public void EveryActionHasItsExpectedAuthorizationTier()
    {
        foreach (var m in ActionMethods())
        {
            Assert.True(
                Expected.ContainsKey(m.Name),
                $"BannerController action '{m.Name}' has no expected auth tier — add it to " +
                "ControllerAuthTests.Expected with a deliberate tier (an unclassified endpoint must not ship).");
            Assert.Equal(Expected[m.Name], ActualTier(m));
        }
    }

    [Fact]
    public void EveryExpectedActionStillExists()
    {
        var names = ActionMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in Expected.Keys)
        {
            Assert.True(
                names.Contains(name),
                $"Expected action '{name}' no longer exists on BannerController (renamed/removed?) — update ControllerAuthTests.Expected.");
        }
    }

    [Fact]
    public void AdminMutatingEndpoints_AreAllElevationGated()
    {
        // Belt-and-suspenders: the write/admin endpoints must be Admin tier, spelled out
        // independently of the map above so a wholesale map edit can't quietly relax them.
        foreach (var name in new[] { "SaveConfig", "SaveMaintenance", "SaveAdminAnnouncements",
            "ResetAnnouncementSeen", "TestWebhook", "GetConfigAdmin", "GetUsersSummary", "GetActiveSessions" })
        {
            var m = typeof(BannerController).GetMethod(name);
            Assert.NotNull(m);
            Assert.Equal(Tier.Admin, ActualTier(m!));
        }
    }
}
