using System;
using System.Collections.Generic;
using System.Globalization;
using Serilog;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Views;

/// <summary>
/// The clash and short-gap check from the local store (schedule-gap-warning R2, R9, R10): only when the active set's
/// switch is on, against the active set's members, leaving out the saved event's own series.
/// </summary>
internal static class ScheduleWarning
{
    /// <summary>The note for an occurrence saved at these UTC times; null when nothing to say or the check is off.</summary>
    public static ScheduleNote? For(LocalStore store, string? itemId, DateTimeOffset startUtc, DateTimeOffset endUtc, bool allDay, ShowAs showAs)
    {
        try
        {
            if (allDay || showAs == ShowAs.Free || endUtc <= startUtc || !store.GetClashCheck(store.ActiveSetId))
            {
                return null;
            }
            var gap = store.GetMinimumGap();
            var margin = TimeSpan.FromMinutes(gap + 1);
            var others = store.GetOccurrenceViews(startUtc - margin, endUtc + margin, includeHidden: true);
            return ScheduleCheck.Check(startUtc, endUtc, allDay, showAs, others, OwnSeries(store, itemId), store.GetActiveSetMembers(),
                gap, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        }
        catch (Exception ex)
        {
            // The check never blocks or breaks a save.
            Log.Warning(ex, "Schedule check for {ItemId} failed", itemId);
            return null;
        }
    }

    /// <summary>The same for wall-clock times in <paramref name="zone"/> (the editor, a drop); null for a time that does not exist.</summary>
    public static ScheduleNote? For(LocalStore store, string? itemId, DateTime start, DateTime end, TimeZoneInfo zone, TimeZoneInfo endZone,
        bool allDay, ShowAs showAs)
    {
        if (allDay || zone.IsInvalidTime(start) || endZone.IsInvalidTime(end))
        {
            return null;
        }
        var from = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(start, DateTimeKind.Unspecified), zone);
        var to = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(end, DateTimeKind.Unspecified), endZone);
        return For(store, itemId, new DateTimeOffset(from), new DateTimeOffset(to), allDay, showAs);
    }

    /// <summary>The item, and for a repeating MS365 event its master and every stored occurrence and exception.</summary>
    private static HashSet<string> OwnSeries(LocalStore store, string? itemId)
    {
        var own = new HashSet<string>();
        if (itemId is null || store.GetItem(itemId) is not { } item)
        {
            return own;
        }
        own.Add(item.Id);
        if ((item.SeriesId ?? (item.IsRecurring ? item.RemoteId : null)) is { } series)
        {
            if (store.FindItemByRemoteId(item.CollectionId, series) is { } master)
            {
                own.Add(master.Id);
            }
            foreach (var s in store.GetSeriesItems(item.CollectionId, series))
            {
                own.Add(s.Id);
            }
        }
        return own;
    }
}
