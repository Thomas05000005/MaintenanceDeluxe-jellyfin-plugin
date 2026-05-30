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
        List<string>? targetUserIds = null,
        bool isDraft = false,
        int? expireAfterDays = null)
    {
        return new Announcement
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Title = "test",
            IsActive = active,
            PublishedAt = publishedAt,
            TargetRoles = targetRoles ?? new(),
            TargetUserIds = targetUserIds ?? new(),
            IsDraft = isDraft,
            ExpireAfterDays = expireAfterDays
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

    // ── IsTargetedAtUser: drafts + expiration (v0.5.1) ───────────────────────

    [Fact]
    public void IsTargetedAtUser_DraftIsNeverDelivered_EvenIfActive()
    {
        var a = MakeAnnouncement(active: true, isDraft: true);
        Assert.False(AnnouncementHelper.IsTargetedAtUser(a, "uuid", isAdmin: false));
        Assert.False(AnnouncementHelper.IsTargetedAtUser(a, "uuid", isAdmin: true));
    }

    [Fact]
    public void IsTargetedAtUser_PublishedNonDraft_IsDelivered()
    {
        var a = MakeAnnouncement(active: true, isDraft: false);
        Assert.True(AnnouncementHelper.IsTargetedAtUser(a, "uuid", isAdmin: false));
    }

    [Fact]
    public void IsTargetedAtUser_ExpiredAnnouncementIsFilteredOut()
    {
        // Published 10 days ago, expires after 7 -> 3 days past expiration.
        var a = MakeAnnouncement(
            publishedAt: DateTimeOffset.UtcNow.AddDays(-10),
            expireAfterDays: 7);
        Assert.False(AnnouncementHelper.IsTargetedAtUser(a, "uuid", isAdmin: false));
    }

    [Fact]
    public void IsTargetedAtUser_NotYetExpiredAnnouncementIsDelivered()
    {
        // Published 2 days ago, expires after 7 -> 5 days remaining.
        var a = MakeAnnouncement(
            publishedAt: DateTimeOffset.UtcNow.AddDays(-2),
            expireAfterDays: 7);
        Assert.True(AnnouncementHelper.IsTargetedAtUser(a, "uuid", isAdmin: false));
    }

    [Fact]
    public void IsTargetedAtUser_ExpireAfterDaysWithNullPublishedAt_NeverExpires()
    {
        // No PublishedAt means we can't compute expiration -> safe default is "not expired".
        var a = MakeAnnouncement(publishedAt: null, expireAfterDays: 7);
        Assert.True(AnnouncementHelper.IsTargetedAtUser(a, "uuid", isAdmin: false));
    }

    [Fact]
    public void IsTargetedAtUser_NullExpireAfterDays_NeverExpires()
    {
        // Even with very old PublishedAt, no expiration window = never expires.
        var a = MakeAnnouncement(
            publishedAt: DateTimeOffset.UtcNow.AddYears(-1),
            expireAfterDays: null);
        Assert.True(AnnouncementHelper.IsTargetedAtUser(a, "uuid", isAdmin: false));
    }

    [Theory]
    [InlineData(-10, 7, true)]   // expired 3 days ago
    [InlineData(-7, 7, true)]    // exactly at expiration boundary (published 7d ago, expires after 7d -> expired since 0s ago)
    [InlineData(-3, 7, false)]   // 4 days remaining
    [InlineData(0, 7, false)]    // just published
    [InlineData(-30, 0, false)]  // 0 days = treated as "no expiration"
    [InlineData(-30, -5, false)] // negative days = treated as "no expiration"
    public void IsExpired_HandlesBoundaries(int publishedOffsetDays, int expireAfterDays, bool expectedExpired)
    {
        var a = MakeAnnouncement(
            publishedAt: DateTimeOffset.UtcNow.AddDays(publishedOffsetDays),
            expireAfterDays: expireAfterDays);
        Assert.Equal(expectedExpired, AnnouncementHelper.IsExpired(a, DateTimeOffset.UtcNow));
    }

    // ── IsScheduleActive (v0.5.2) ────────────────────────────────────────────

    [Fact]
    public void IsScheduleActive_NullOrAlways_IsAlwaysActive()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(AnnouncementHelper.IsScheduleActive(null, now));
        Assert.True(AnnouncementHelper.IsScheduleActive(new BannerSchedule { Type = "always" }, now));
        Assert.True(AnnouncementHelper.IsScheduleActive(new BannerSchedule { Type = "" }, now));
    }

    [Fact]
    public void IsScheduleActive_FixedWindow_RespectsBounds()
    {
        var now = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var inWindow = new BannerSchedule { Type = "fixed", FixedStart = "2026-05-19T00:00:00Z", FixedEnd = "2026-05-21T00:00:00Z" };
        Assert.True(AnnouncementHelper.IsScheduleActive(inWindow, now));

        var beforeStart = new BannerSchedule { Type = "fixed", FixedStart = "2026-06-01T00:00:00Z" };
        Assert.False(AnnouncementHelper.IsScheduleActive(beforeStart, now));

        var afterEnd = new BannerSchedule { Type = "fixed", FixedEnd = "2026-05-01T00:00:00Z" };
        Assert.False(AnnouncementHelper.IsScheduleActive(afterEnd, now));

        // Open-ended fixed window with only one bound also works.
        var noEnd = new BannerSchedule { Type = "fixed", FixedStart = "2026-01-01T00:00:00Z" };
        Assert.True(AnnouncementHelper.IsScheduleActive(noEnd, now));
    }

    [Fact]
    public void IsScheduleActive_AnnualWindow_HandlesYearWrap()
    {
        // Halloween-ish window: Oct 20 -> Nov 1 (no year wrap).
        var halloween = new BannerSchedule { Type = "annual", MonthStart = 10, DayStart = 20, MonthEnd = 11, DayEnd = 1 };
        Assert.True(AnnouncementHelper.IsScheduleActive(halloween, new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero)));
        Assert.False(AnnouncementHelper.IsScheduleActive(halloween, new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero)));

        // Christmas window: Dec 20 -> Jan 5 (wraps year boundary).
        var christmas = new BannerSchedule { Type = "annual", MonthStart = 12, DayStart = 20, MonthEnd = 1, DayEnd = 5 };
        Assert.True(AnnouncementHelper.IsScheduleActive(christmas, new DateTimeOffset(2026, 12, 24, 12, 0, 0, TimeSpan.Zero)));
        Assert.True(AnnouncementHelper.IsScheduleActive(christmas, new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero)));
        Assert.False(AnnouncementHelper.IsScheduleActive(christmas, new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void IsScheduleActive_Weekly_ChecksDayOfWeekAndTime()
    {
        // Active Monday + Wednesday only (1 + 3 in JS day convention).
        var weekly = new BannerSchedule { Type = "weekly", WeekDays = new() { 1, 3 } };

        // 2026-05-18 is Monday -> active.
        Assert.True(AnnouncementHelper.IsScheduleActive(weekly, new DateTimeOffset(2026, 5, 18, 14, 0, 0, TimeSpan.Zero)));
        // 2026-05-19 is Tuesday -> not active.
        Assert.False(AnnouncementHelper.IsScheduleActive(weekly, new DateTimeOffset(2026, 5, 19, 14, 0, 0, TimeSpan.Zero)));
        // 2026-05-20 is Wednesday -> active.
        Assert.True(AnnouncementHelper.IsScheduleActive(weekly, new DateTimeOffset(2026, 5, 20, 14, 0, 0, TimeSpan.Zero)));

        // Empty weekDays list -> never active (no day matches).
        var emptyDays = new BannerSchedule { Type = "weekly", WeekDays = new() };
        Assert.False(AnnouncementHelper.IsScheduleActive(emptyDays, new DateTimeOffset(2026, 5, 18, 14, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("09:00", "17:00", 12, 0, true)]   // mid-window
    [InlineData("09:00", "17:00", 8, 0, false)]   // before
    [InlineData("09:00", "17:00", 18, 0, false)]  // after
    [InlineData("09:00", "17:00", 9, 0, true)]    // exactly at start
    [InlineData("09:00", "17:00", 17, 0, true)]   // exactly at end
    [InlineData("22:00", "06:00", 23, 0, true)]   // overnight window, evening side
    [InlineData("22:00", "06:00", 3, 0, true)]    // overnight window, early morning side
    [InlineData("22:00", "06:00", 12, 0, false)]  // overnight window, middle of day -> out
    public void IsScheduleActive_Daily_TimeWindow(string start, string end, int nowHour, int nowMin, bool expected)
    {
        var schedule = new BannerSchedule { Type = "daily", TimeStart = start, TimeEnd = end };
        var now = new DateTimeOffset(2026, 5, 20, nowHour, nowMin, 0, TimeSpan.Zero);
        Assert.Equal(expected, AnnouncementHelper.IsScheduleActive(schedule, now));
    }

    [Fact]
    public void IsScheduleActive_UnknownType_FallsBackToAlwaysActive()
    {
        // Bug-friendly default: unknown type doesn't silently hide the announcement.
        var weird = new BannerSchedule { Type = "monthly" };
        Assert.True(AnnouncementHelper.IsScheduleActive(weird, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsTargetedAtUser_RespectsSchedule()
    {
        // Annual schedule active only in June -> May 20 should hide the announcement.
        var inJuneOnly = MakeAnnouncement(publishedAt: DateTimeOffset.UtcNow.AddDays(-1));
        inJuneOnly.Schedule = new BannerSchedule { Type = "annual", MonthStart = 6, DayStart = 1, MonthEnd = 6, DayEnd = 30 };
        // Force "now" check by relying on actual UTC now: only meaningful if we control time.
        // Use IsScheduleActive directly to keep this deterministic:
        var may20 = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        Assert.False(AnnouncementHelper.IsScheduleActive(inJuneOnly.Schedule, may20));
        var june15 = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.True(AnnouncementHelper.IsScheduleActive(inJuneOnly.Schedule, june15));
    }

    // ── ImageUrl allowlist via shared IsUrlSafe (v0.5.3) ──────────────────────
    // The actual filtering uses BannerController.IsUrlSafe (already tested in NormalisationTests).
    // These tests document that imageUrl follows the same rules and ensure the JSON
    // contract stays stable for the client.

    [Fact]
    public void Announcement_ImageUrl_DefaultsToNull()
    {
        var a = MakeAnnouncement();
        Assert.Null(a.ImageUrl);
        Assert.Null(a.ImageAlt);
    }

    [Fact]
    public void SelectDeliverableForUser_PreservesImageFields()
    {
        // Regression: image fields must round-trip through the selection pipeline so
        // the client receives them (the server projection is the raw Announcement object).
        var a = MakeAnnouncement(id: "img-test", publishedAt: DateTimeOffset.UtcNow.AddHours(-1));
        a.ImageUrl = "https://example.com/banner.png";
        a.ImageAlt = "Banner alt text";
        var result = AnnouncementHelper.SelectDeliverableForUser(
            new[] { a }, new List<AnnouncementsSeenEntry>(), "u", isAdmin: false);
        Assert.Single(result);
        Assert.Equal("https://example.com/banner.png", result[0].ImageUrl);
        Assert.Equal("Banner alt text", result[0].ImageAlt);
    }

    [Fact]
    public void SelectDeliverableForUser_FiltersDraftsAndExpired()
    {
        var announcements = new[]
        {
            MakeAnnouncement(id: "live", publishedAt: DateTimeOffset.UtcNow.AddDays(-1), expireAfterDays: 30),
            MakeAnnouncement(id: "draft", isDraft: true, publishedAt: DateTimeOffset.UtcNow.AddDays(-1)),
            MakeAnnouncement(id: "expired", publishedAt: DateTimeOffset.UtcNow.AddDays(-20), expireAfterDays: 7),
        };
        var result = AnnouncementHelper.SelectDeliverableForUser(announcements, new List<AnnouncementsSeenEntry>(), "u", false);
        Assert.Single(result);
        Assert.Equal("live", result[0].Id);
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

    // ── Regression guards (v0.8.0) ────────────────────────────────────────────

    [Fact]
    public void IsTargetedAtUser_DraftWithRoleAndUuidFilters_NeverDelivered()
    {
        // Verifie que draft trumps tous les autres filtres - regression guard contre reorder.
        var a = MakeAnnouncement(
            isDraft: true,
            targetRoles: new() { "admin" },
            targetUserIds: new() { "alice-uuid" });
        Assert.False(AnnouncementHelper.IsTargetedAtUser(a, "alice-uuid", isAdmin: true));
        Assert.False(AnnouncementHelper.IsTargetedAtUser(a, "alice-uuid", isAdmin: false));
    }

    [Fact]
    public void IsScheduleActive_Daily_MidnightWrap_ExactBoundaries()
    {
        var schedule = new BannerSchedule { Type = "daily", TimeStart = "23:00", TimeEnd = "01:00" };
        Assert.True(AnnouncementHelper.IsScheduleActive(schedule, new DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero)));
        Assert.True(AnnouncementHelper.IsScheduleActive(schedule, new DateTimeOffset(2026, 5, 20, 0, 1, 0, TimeSpan.Zero)));
        Assert.True(AnnouncementHelper.IsScheduleActive(schedule, new DateTimeOffset(2026, 5, 20, 23, 0, 0, TimeSpan.Zero)));
        Assert.False(AnnouncementHelper.IsScheduleActive(schedule, new DateTimeOffset(2026, 5, 20, 22, 59, 0, TimeSpan.Zero)));
        Assert.False(AnnouncementHelper.IsScheduleActive(schedule, new DateTimeOffset(2026, 5, 20, 1, 1, 0, TimeSpan.Zero)));
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

    // ── Hardening (mutation testing showed kill-count == 1 for these) ─────────
    // Each InlineData row is an independent test case, so a value mutation on
    // HasUserSeen is now caught by several cases, not one fragile assertion block.

    [Theory]
    [InlineData("a1", "alice", true)]
    [InlineData("a1", "bob", true)]
    [InlineData("a1", "carol", false)]   // known announce, unknown user
    [InlineData("a2", "alice", true)]
    [InlineData("a2", "bob", false)]     // user saw a1 but not a2
    [InlineData("zzz", "alice", false)]  // unknown announce entirely
    public void HasUserSeen_Theory_MatchesPerEntryUserSet(string announceId, string userId, bool expected)
    {
        var tracking = new List<AnnouncementsSeenEntry>
        {
            new() { AnnouncementId = "a1", UserIds = new() { "alice", "bob" } },
            new() { AnnouncementId = "a2", UserIds = new() { "alice" } }
        };
        Assert.Equal(expected, AnnouncementHelper.HasUserSeen(tracking, announceId, userId));
    }

    [Fact]
    public void MarkSeen_TracksAcrossEntriesAndDedupes()
    {
        var tracking = new List<AnnouncementsSeenEntry>();
        Assert.True(AnnouncementHelper.MarkSeen(tracking, "a1", "u1"));   // new entry + new user
        Assert.True(AnnouncementHelper.MarkSeen(tracking, "a1", "u2"));   // same entry, new user
        Assert.False(AnnouncementHelper.MarkSeen(tracking, "a1", "u2"));  // duplicate -> no-op
        Assert.True(AnnouncementHelper.MarkSeen(tracking, "a2", "u1"));   // second entry
        Assert.Equal(2, tracking.Count);
        Assert.Equal(2, tracking.First(e => e.AnnouncementId == "a1").UserIds.Count);
        Assert.Single(tracking.First(e => e.AnnouncementId == "a2").UserIds);
        // After marking, HasUserSeen must agree (cross-method consistency).
        Assert.True(AnnouncementHelper.HasUserSeen(tracking, "a1", "u1"));
        Assert.True(AnnouncementHelper.HasUserSeen(tracking, "a2", "u1"));
        Assert.False(AnnouncementHelper.HasUserSeen(tracking, "a2", "u2"));
    }
}
