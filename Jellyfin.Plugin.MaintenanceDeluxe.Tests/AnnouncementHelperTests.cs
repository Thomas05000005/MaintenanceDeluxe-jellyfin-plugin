using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MaintenanceDeluxe.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MaintenanceDeluxe.Tests;

public class AnnouncementHelperTests
{
    private static Announcement MakeAnnouncement(
        string? id = null,
        bool active = true,
        DateTimeOffset? publishedAt = null,
        List<string>? targetRoles = null,
        List<string>? targetUserIds = null)
    {
        return new Announcement
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Title = "test",
            IsActive = active,
            PublishedAt = publishedAt,
            TargetRoles = targetRoles ?? new(),
            TargetUserIds = targetUserIds ?? new()
        };
    }

    // ── IsTargetedAtUser ─────────────────────────────────────────────────────

    [Fact]
    public void IsTargetedAtUser_InactiveAnnouncement_ReturnsFalse()
    {
        var a = MakeAnnouncement(active: false);
        Assert.False(AnnouncementHelper.IsTargetedAtUser(a, "any-uuid", false));
        Assert.False(AnnouncementHelper.IsTargetedAtUser(a, "any-uuid", true));
    }

    [Fact]
    public void IsTargetedAtUser_NoFilters_MatchesEveryone()
    {
        var a = MakeAnnouncement();
        Assert.True(AnnouncementHelper.IsTargetedAtUser(a, "uuid-1", isAdmin: true));
        Assert.True(AnnouncementHelper.IsTargetedAtUser(a, "uuid-2", isAdmin: false));
    }

    [Fact]
    public void IsTargetedAtUser_RoleFilter_AdminOnly()
    {
        var a = MakeAnnouncement(targetRoles: new() { "admin" });
        Assert.True(AnnouncementHelper.IsTargetedAtUser(a, "uuid-1", isAdmin: true));
        Assert.False(AnnouncementHelper.IsTargetedAtUser(a, "uuid-1", isAdmin: false));
    }

    [Fact]
    public void IsTargetedAtUser_RoleFilter_UserOnly()
    {
        var a = MakeAnnouncement(targetRoles: new() { "user" });
        Assert.False(AnnouncementHelper.IsTargetedAtUser(a, "uuid-1", isAdmin: true));
        Assert.True(AnnouncementHelper.IsTargetedAtUser(a, "uuid-1", isAdmin: false));
    }

    [Fact]
    public void IsTargetedAtUser_RoleFilter_BothListed_MatchesAnyone()
    {
        var a = MakeAnnouncement(targetRoles: new() { "user", "admin" });
        Assert.True(AnnouncementHelper.IsTargetedAtUser(a, "uuid-1", isAdmin: true));
        Assert.True(AnnouncementHelper.IsTargetedAtUser(a, "uuid-2", isAdmin: false));
    }

    [Fact]
    public void IsTargetedAtUser_UuidFilter_OnlySpecificUser()
    {
        var a = MakeAnnouncement(targetUserIds: new() { "alice-uuid" });
        Assert.True(AnnouncementHelper.IsTargetedAtUser(a, "alice-uuid", isAdmin: false));
        Assert.False(AnnouncementHelper.IsTargetedAtUser(a, "bob-uuid", isAdmin: false));
    }

    [Fact]
    public void IsTargetedAtUser_RoleAndUuid_BothMustMatch()
    {
        // "any admin who is alice" - intersection
        var a = MakeAnnouncement(
            targetRoles: new() { "admin" },
            targetUserIds: new() { "alice-uuid" });
        Assert.True(AnnouncementHelper.IsTargetedAtUser(a, "alice-uuid", isAdmin: true));
        Assert.False(AnnouncementHelper.IsTargetedAtUser(a, "alice-uuid", isAdmin: false)); // alice but not admin
        Assert.False(AnnouncementHelper.IsTargetedAtUser(a, "bob-uuid", isAdmin: true));    // admin but not alice
    }

    // ── HasUserSeen ──────────────────────────────────────────────────────────

    [Fact]
    public void HasUserSeen_TracksDismissalsCorrectly()
    {
        var tracking = new List<AnnouncementsSeenEntry>
        {
            new() { AnnouncementId = "a1", UserIds = new() { "alice", "bob" } },
            new() { AnnouncementId = "a2", UserIds = new() { "alice" } }
        };

        Assert.True(AnnouncementHelper.HasUserSeen(tracking, "a1", "alice"));
        Assert.True(AnnouncementHelper.HasUserSeen(tracking, "a1", "bob"));
        Assert.False(AnnouncementHelper.HasUserSeen(tracking, "a1", "carol"));
        Assert.True(AnnouncementHelper.HasUserSeen(tracking, "a2", "alice"));
        Assert.False(AnnouncementHelper.HasUserSeen(tracking, "a2", "bob"));
        Assert.False(AnnouncementHelper.HasUserSeen(tracking, "a3", "alice")); // unknown announce
    }

    // ── SelectDeliverableForUser ─────────────────────────────────────────────

    [Fact]
    public void SelectDeliverableForUser_HappyPath_ReturnsOnlyTargetedAndUnseen()
    {
        var announcements = new[]
        {
            MakeAnnouncement(id: "a1", publishedAt: DateTimeOffset.UtcNow.AddDays(-3)),
            MakeAnnouncement(id: "a2", publishedAt: DateTimeOffset.UtcNow.AddDays(-1)),
            MakeAnnouncement(id: "a3", publishedAt: DateTimeOffset.UtcNow.AddDays(-2)),
        };
        var seen = new List<AnnouncementsSeenEntry>
        {
            new() { AnnouncementId = "a3", UserIds = new() { "alice" } }
        };

        var result = AnnouncementHelper.SelectDeliverableForUser(announcements, seen, "alice", isAdmin: false);

        // a3 filtered out (seen); a2 newest first, then a1
        Assert.Equal(2, result.Count);
        Assert.Equal("a2", result[0].Id);
        Assert.Equal("a1", result[1].Id);
    }

    [Fact]
    public void SelectDeliverableForUser_FiltersByRole()
    {
        var announcements = new[]
        {
            MakeAnnouncement(id: "all"),
            MakeAnnouncement(id: "users-only", targetRoles: new() { "user" }),
            MakeAnnouncement(id: "admins-only", targetRoles: new() { "admin" })
        };
        var seen = new List<AnnouncementsSeenEntry>();

        var asAdmin = AnnouncementHelper.SelectDeliverableForUser(announcements, seen, "u", isAdmin: true);
        var asUser = AnnouncementHelper.SelectDeliverableForUser(announcements, seen, "u", isAdmin: false);

        Assert.Equal(2, asAdmin.Count);
        Assert.Contains(asAdmin, a => a.Id == "all");
        Assert.Contains(asAdmin, a => a.Id == "admins-only");

        Assert.Equal(2, asUser.Count);
        Assert.Contains(asUser, a => a.Id == "all");
        Assert.Contains(asUser, a => a.Id == "users-only");
    }

    [Fact]
    public void SelectDeliverableForUser_InactiveAnnouncementsExcluded()
    {
        var announcements = new[]
        {
            MakeAnnouncement(id: "active"),
            MakeAnnouncement(id: "inactive", active: false)
        };

        var result = AnnouncementHelper.SelectDeliverableForUser(announcements, new List<AnnouncementsSeenEntry>(), "u", false);
        Assert.Single(result);
        Assert.Equal("active", result[0].Id);
    }

    [Fact]
    public void SelectDeliverableForUser_NullPublishedDates_OrderedByIdAsTieBreaker()
    {
        var announcements = new[]
        {
            MakeAnnouncement(id: "zzz"),
            MakeAnnouncement(id: "aaa"),
            MakeAnnouncement(id: "mmm")
        };
        var result = AnnouncementHelper.SelectDeliverableForUser(announcements, new List<AnnouncementsSeenEntry>(), "u", false);
        Assert.Equal("aaa", result[0].Id);
        Assert.Equal("mmm", result[1].Id);
        Assert.Equal("zzz", result[2].Id);
    }

    [Fact]
    public void SelectDeliverableForUser_NullUserIdsInSeenEntry_DoesNotCrash()
    {
        // Regression guard for the HashSet-based perf rewrite: a malformed entry where
        // UserIds is null (older config schema) must be tolerated, not throw NRE.
        var announcements = new[] { MakeAnnouncement(id: "a1") };
        var seen = new List<AnnouncementsSeenEntry>
        {
            new() { AnnouncementId = "a1", UserIds = null! }
        };
        var result = AnnouncementHelper.SelectDeliverableForUser(announcements, seen, "alice", false);
        Assert.Single(result); // alice hasn't seen it (null list = no users)
    }

    [Fact]
    public void SelectDeliverableForUser_ManyAnnouncementsAndSeenEntries_StaysCorrect()
    {
        // Stress-test the O(1) lookup rewrite: 100 announcements × 50 seen entries.
        // Validates the optimisation didn't change behaviour.
        var announcements = new List<Announcement>();
        for (var i = 0; i < 100; i++)
            announcements.Add(MakeAnnouncement(id: $"a{i:D3}"));

        var seen = new List<AnnouncementsSeenEntry>();
        // alice has seen every even-numbered announcement.
        for (var i = 0; i < 100; i += 2)
            seen.Add(new AnnouncementsSeenEntry { AnnouncementId = $"a{i:D3}", UserIds = new() { "alice" } });

        var result = AnnouncementHelper.SelectDeliverableForUser(announcements, seen, "alice", false);
        Assert.Equal(50, result.Count); // 50 odd-numbered ones remain
        Assert.All(result, a => Assert.True(int.Parse(a.Id[1..]) % 2 == 1, "Only odd-numbered announcements should remain unseen"));
        Assert.DoesNotContain(result, a => seen.Any(s => s.AnnouncementId == a.Id));
    }

    // ── MarkSeen / ResetSeen ─────────────────────────────────────────────────

    [Fact]
    public void MarkSeen_CreatesEntryAndIsIdempotent()
    {
        var tracking = new List<AnnouncementsSeenEntry>();

        Assert.True(AnnouncementHelper.MarkSeen(tracking, "a1", "alice"));
        Assert.Single(tracking);
        Assert.Equal("a1", tracking[0].AnnouncementId);
        Assert.Contains("alice", tracking[0].UserIds);

        // Re-marking same user: no change, return false
        Assert.False(AnnouncementHelper.MarkSeen(tracking, "a1", "alice"));
        Assert.Single(tracking[0].UserIds);

        // Different user on same announcement
        Assert.True(AnnouncementHelper.MarkSeen(tracking, "a1", "bob"));
        Assert.Equal(2, tracking[0].UserIds.Count);
    }

    [Fact]
    public void ResetSeen_RemovesEntireEntry()
    {
        var tracking = new List<AnnouncementsSeenEntry>
        {
            new() { AnnouncementId = "a1", UserIds = new() { "alice", "bob" } },
            new() { AnnouncementId = "a2", UserIds = new() { "alice" } }
        };

        Assert.True(AnnouncementHelper.ResetSeen(tracking, "a1"));
        Assert.Single(tracking);
        Assert.Equal("a2", tracking[0].AnnouncementId);

        // Idempotent: reset on missing id returns false
        Assert.False(AnnouncementHelper.ResetSeen(tracking, "a1"));
    }

    [Fact]
    public void PruneOrphanedSeenEntries_DropsTrackingForDeletedAnnouncements()
    {
        var announcements = new[]
        {
            MakeAnnouncement(id: "kept-1"),
            MakeAnnouncement(id: "kept-2")
        };
        var tracking = new List<AnnouncementsSeenEntry>
        {
            new() { AnnouncementId = "kept-1", UserIds = new() { "alice" } },
            new() { AnnouncementId = "orphan-old", UserIds = new() { "bob" } },
            new() { AnnouncementId = "kept-2", UserIds = new() { "carol" } },
            new() { AnnouncementId = "orphan-old-2", UserIds = new() { "dave" } }
        };

        var pruned = AnnouncementHelper.PruneOrphanedSeenEntries(tracking, announcements);

        Assert.Equal(2, pruned);
        Assert.Equal(2, tracking.Count);
        Assert.All(tracking, e => Assert.Contains(e.AnnouncementId, new[] { "kept-1", "kept-2" }));
    }

    // ── Normalisation ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("info", "info")]
    [InlineData("update", "update")]
    [InlineData("warning", "warning")]
    [InlineData("critical", "critical")]
    [InlineData("Info", "info")]      // case-sensitive whitelist → fallback
    [InlineData("urgent", "info")]    // unknown
    [InlineData("", "info")]
    [InlineData(null, "info")]
    public void NormaliseImportance(string? input, string expected)
    {
        Assert.Equal(expected, AnnouncementHelper.NormaliseImportance(input));
    }

    [Theory]
    [InlineData("one-at-a-time", "one-at-a-time")]
    [InlineData("carousel", "carousel")]
    [InlineData("stack", "stack")]
    [InlineData("Stack", "one-at-a-time")]
    [InlineData("", "one-at-a-time")]
    [InlineData(null, "one-at-a-time")]
    public void NormaliseMultiMode(string? input, string expected)
    {
        Assert.Equal(expected, AnnouncementHelper.NormaliseMultiMode(input));
    }

    [Fact]
    public void NormaliseTargetRoles_FiltersUnknownAndDeduplicates()
    {
        var result = AnnouncementHelper.NormaliseTargetRoles(new[] { "user", "admin", "user", "moderator", "", "ADMIN" });
        Assert.Equal(2, result.Count);
        Assert.Contains("user", result);
        Assert.Contains("admin", result);
    }

    [Fact]
    public void NormaliseTargetRoles_NullInput_ReturnsEmpty()
    {
        Assert.Empty(AnnouncementHelper.NormaliseTargetRoles(null));
    }

    [Fact]
    public void NormaliseTargetUserIds_FiltersMalformedAndDeduplicates()
    {
        var validGuid = Guid.NewGuid().ToString();
        var result = AnnouncementHelper.NormaliseTargetUserIds(new[]
        {
            validGuid,
            "not-a-guid",
            validGuid,                                  // duplicate
            Guid.NewGuid().ToString(),                  // another valid
            "",
            "00000000-0000-0000-0000"                   // malformed
        });

        Assert.Equal(2, result.Count);
        Assert.Contains(validGuid, result);
    }
}
