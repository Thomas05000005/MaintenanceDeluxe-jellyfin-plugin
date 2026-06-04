using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MaintenanceDeluxe.Configuration;

namespace Jellyfin.Plugin.MaintenanceDeluxe;

/// <summary>
/// Pure helpers for the announcement system. Kept separate from the controller so
/// the filtering / targeting / seen-tracking logic can be unit-tested without
/// instantiating Plugin.Instance or mocking IUserManager.
/// </summary>
internal static class AnnouncementHelper
{
    /// <summary>Returns true if the announcement targets the given user. The targeting model is:
    /// (1) <c>IsActive</c> must be true AND <c>IsDraft</c> must be false,
    /// (2) the announcement must not be expired (see <see cref="IsExpired"/>),
    /// (3) the announcement's <c>Schedule</c> must match the current moment (see <see cref="IsScheduleActive"/>),
    /// (4) if <c>TargetRoles</c> is non-empty, the user's role must be in the list (recognised: "user", "admin"),
    /// (5) if <c>TargetUserIds</c> is non-empty, the user's UUID must be in the list.
    /// An empty list at (4) or (5) means "no filter on that dimension". All filters are AND-combined.</summary>
    internal static bool IsTargetedAtUser(Announcement a, string userId, bool isAdmin)
        => IsTargetedAtUser(a, userId, isAdmin, DateTimeOffset.Now);

    /// <summary>Overload with an explicit evaluation moment (for tests). Production callers use the
    /// no-<paramref name="now"/> overload, which passes <see cref="DateTimeOffset.Now"/> — the
    /// SERVER's LOCAL time. This matters for recurring schedules (daily/weekly/annual + time
    /// windows): the admin types wall-clock times meaning their local clock, and the visually
    /// identical banner schedules in banner.js are evaluated in the VIEWER's browser-local time.
    /// Evaluating announcements in UTC (the pre-v0.8.4 bug) made recurring announcements fire at
    /// the wrong hours/days on any non-UTC server and diverge from the banners. Server-local time
    /// is the closest single reference frame we can use server-side (we don't know each viewer's
    /// timezone here); set the container's TZ env var to your timezone. Absolute checks
    /// (<see cref="IsExpired"/> and the "fixed" schedule) use offset-aware DateTimeOffset
    /// comparisons, so they are unaffected by the local-vs-UTC choice.</summary>
    internal static bool IsTargetedAtUser(Announcement a, string userId, bool isAdmin, DateTimeOffset now)
    {
        if (!a.IsActive) return false;
        if (a.IsDraft) return false;
        if (IsExpired(a, now)) return false;
        if (!IsScheduleActive(a.Schedule, now)) return false;

        if (a.TargetRoles is { Count: > 0 })
        {
            var wantsAdmin = a.TargetRoles.Contains("admin");
            var wantsUser = a.TargetRoles.Contains("user");
            if (isAdmin && !wantsAdmin) return false;
            if (!isAdmin && !wantsUser) return false;
        }

        if (a.TargetUserIds is { Count: > 0 })
        {
            if (!a.TargetUserIds.Contains(userId)) return false;
        }

        return true;
    }

    /// <summary>Returns true when an announcement has an <see cref="Announcement.ExpireAfterDays"/>
    /// value set, a non-null <see cref="Announcement.PublishedAt"/>, and the expiration moment
    /// (<c>PublishedAt + ExpireAfterDays</c>) is before <paramref name="now"/>. Stateless — does
    /// not mutate the announcement (no auto-archive); admins still see expired items in the list
    /// with an "Expirée" badge so they can clean up or re-publish.</summary>
    internal static bool IsExpired(Announcement a, DateTimeOffset now)
    {
        if (a.ExpireAfterDays is not int days || days <= 0) return false;
        if (a.PublishedAt is not DateTimeOffset publishedAt) return false;
        return publishedAt.AddDays(days) < now;
    }

    /// <summary>Returns true when a <see cref="BannerSchedule"/> is currently active at
    /// <paramref name="now"/>. Mirrors the JS <c>isInSchedule</c> in banner.js so that
    /// banner messages (filtered client-side) and announcements (filtered server-side via
    /// this method) behave identically. Null schedule, missing type, or type="always" all
    /// return true (no time filter).</summary>
    internal static bool IsScheduleActive(BannerSchedule? schedule, DateTimeOffset now)
    {
        if (schedule is null || string.IsNullOrEmpty(schedule.Type) || schedule.Type == "always") return true;

        switch (schedule.Type)
        {
            case "fixed":
                if (!string.IsNullOrEmpty(schedule.FixedStart)
                    && DateTimeOffset.TryParse(schedule.FixedStart, out var fs)
                    && now < fs) return false;
                if (!string.IsNullOrEmpty(schedule.FixedEnd)
                    && DateTimeOffset.TryParse(schedule.FixedEnd, out var fe)
                    && now > fe) return false;
                return true;

            case "annual":
                {
                    var ms = schedule.MonthStart;
                    var ds = schedule.DayStart;
                    var me = schedule.MonthEnd;
                    var de = schedule.DayEnd;
                    if (ms is null || ds is null || me is null || de is null)
                    {
                        // No month/day range -> only the time-of-day window applies.
                        return MatchesTimeWindow(now, schedule.TimeStart, schedule.TimeEnd);
                    }
                    var nowMd = (now.Month * 100) + now.Day;
                    var startMd = (ms.Value * 100) + ds.Value;
                    var endMd = (me.Value * 100) + de.Value;
                    // Range may wrap the year boundary (Dec 20 -> Jan 5).
                    var inRange = startMd <= endMd
                        ? nowMd >= startMd && nowMd <= endMd
                        : nowMd >= startMd || nowMd <= endMd;
                    return inRange && MatchesTimeWindow(now, schedule.TimeStart, schedule.TimeEnd);
                }

            case "weekly":
                if (schedule.WeekDays is null || schedule.WeekDays.Count == 0) return false;
                // BannerSchedule.WeekDays uses 0=Sunday convention (matching JS Date.getDay()).
                var jsWeekDay = (int)now.DayOfWeek; // C#: Sunday=0 .. Saturday=6, same as JS.
                if (!schedule.WeekDays.Contains(jsWeekDay)) return false;
                return MatchesTimeWindow(now, schedule.TimeStart, schedule.TimeEnd);

            case "daily":
                return MatchesTimeWindow(now, schedule.TimeStart, schedule.TimeEnd);

            default:
                // Unknown type -> treat as "always" so a config bug doesn't silently hide every
                // announcement; the admin will notice the unknown badge instead.
                return true;
        }
    }

    /// <summary>True if <paramref name="now"/> falls within the inclusive HH:MM window
    /// [<paramref name="timeStart"/>, <paramref name="timeEnd"/>]. Empty/missing bounds default
    /// to "always within". Mirrors the JS <c>checkTimeWindow</c> helper.</summary>
    private static bool MatchesTimeWindow(DateTimeOffset now, string? timeStart, string? timeEnd)
    {
        if (string.IsNullOrEmpty(timeStart) && string.IsNullOrEmpty(timeEnd)) return true;
        var nowMinutes = (now.Hour * 60) + now.Minute;
        var startMinutes = ParseHhMm(timeStart) ?? 0;
        var endMinutes = ParseHhMm(timeEnd) ?? (23 * 60 + 59);
        // Same-day window (08:00-18:00): in if start <= now <= end.
        // Overnight window (22:00-06:00): in if now >= start OR now <= end.
        return startMinutes <= endMinutes
            ? nowMinutes >= startMinutes && nowMinutes <= endMinutes
            : nowMinutes >= startMinutes || nowMinutes <= endMinutes;
    }

    private static int? ParseHhMm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var parts = s.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return null;
        if (h < 0 || h > 23 || m < 0 || m > 59) return null;
        return (h * 60) + m;
    }

    /// <summary>Returns true if the given user has already seen the given announcement.
    /// Pure lookup against the seen-tracking list — no I/O.</summary>
    internal static bool HasUserSeen(
        IEnumerable<AnnouncementsSeenEntry> seenTracking,
        string announcementId,
        string userId)
    {
        return seenTracking
            .Where(e => e.AnnouncementId == announcementId)
            .Any(e => e.UserIds.Contains(userId));
    }

    /// <summary>Returns the list of announcements that should be delivered to the given user:
    /// (a) targeted at them by role / UUID filters, (b) not already seen by them.
    /// Sorted by <see cref="Announcement.PublishedAt"/> descending so the most recent ones
    /// come first (or by insertion order when PublishedAt is null on both).</summary>
    internal static List<Announcement> SelectDeliverableForUser(
        IEnumerable<Announcement> announcements,
        IEnumerable<AnnouncementsSeenEntry> seenTracking,
        string userId,
        bool isAdmin)
    {
        // Build an O(1) lookup once: announcementId -> bool "has this user seen it".
        // Without this, SelectDeliverableForUser was O(announcements × seenEntries × usersPerEntry),
        // which got slow for instances with many announcements × many users (1000 users × 50
        // announcements = 50k string comparisons per /announcements/active call).
        var seenForUser = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in seenTracking)
        {
            if (entry.UserIds is not null && entry.UserIds.Contains(userId))
                seenForUser.Add(entry.AnnouncementId);
        }
        return announcements
            .Where(a => IsTargetedAtUser(a, userId, isAdmin))
            .Where(a => !seenForUser.Contains(a.Id))
            .OrderByDescending(a => a.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenBy(a => a.Id, StringComparer.Ordinal) // tie-breaker for deterministic order
            .ToList();
    }

    /// <summary>Marks an announcement as seen by the given user. Creates the seen entry if
    /// missing. Idempotent — calling twice with the same user has no extra effect.
    /// Mutates the input list. Returns true if anything was actually added.</summary>
    internal static bool MarkSeen(
        List<AnnouncementsSeenEntry> seenTracking,
        string announcementId,
        string userId)
    {
        var entry = seenTracking.FirstOrDefault(e => e.AnnouncementId == announcementId);
        if (entry is null)
        {
            entry = new AnnouncementsSeenEntry { AnnouncementId = announcementId };
            seenTracking.Add(entry);
        }
        if (entry.UserIds.Contains(userId)) return false;
        entry.UserIds.Add(userId);
        return true;
    }

    /// <summary>Removes the seen-tracking entry for an announcement so all users will see it
    /// again on their next login. Used by the admin "Reset seen-by" button.
    /// Returns true if an entry existed and was removed.</summary>
    internal static bool ResetSeen(List<AnnouncementsSeenEntry> seenTracking, string announcementId)
    {
        var removed = seenTracking.RemoveAll(e => e.AnnouncementId == announcementId);
        return removed > 0;
    }

    /// <summary>Removes seen-tracking entries that reference announcement IDs no longer in
    /// the announcements list. Garbage-collects stale tracking when admins delete announcements.
    /// Mutates the input list. Returns the count removed.</summary>
    internal static int PruneOrphanedSeenEntries(
        List<AnnouncementsSeenEntry> seenTracking,
        IEnumerable<Announcement> announcements)
    {
        var validIds = announcements.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        return seenTracking.RemoveAll(e => !validIds.Contains(e.AnnouncementId));
    }

    private static readonly HashSet<string> _validImportances =
        new(StringComparer.Ordinal) { "info", "update", "warning", "critical" };

    /// <summary>Whitelists the importance level; fallback "info".</summary>
    internal static string NormaliseImportance(string? value)
    {
        if (!string.IsNullOrEmpty(value) && _validImportances.Contains(value)) return value;
        return "info";
    }

    private static readonly HashSet<string> _validMultiModes =
        new(StringComparer.Ordinal) { "one-at-a-time", "carousel", "stack" };

    /// <summary>Whitelists the multi-announcement display mode; fallback "one-at-a-time".</summary>
    internal static string NormaliseMultiMode(string? value)
    {
        if (!string.IsNullOrEmpty(value) && _validMultiModes.Contains(value)) return value;
        return "one-at-a-time";
    }

    private static readonly HashSet<string> _validRoles =
        new(StringComparer.Ordinal) { "user", "admin" };

    /// <summary>Strips unknown role names and deduplicates. Empty / null input returns empty list.</summary>
    internal static List<string> NormaliseTargetRoles(IEnumerable<string>? input)
    {
        if (input is null) return new List<string>();
        return input
            .Where(r => !string.IsNullOrEmpty(r))
            .Where(_validRoles.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Filters out malformed UUIDs and deduplicates. Empty / null input returns empty list.</summary>
    internal static List<string> NormaliseTargetUserIds(IEnumerable<string>? input)
    {
        if (input is null) return new List<string>();
        return input
            .Where(id => !string.IsNullOrEmpty(id) && Guid.TryParse(id, out _))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
