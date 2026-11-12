using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace WindowsCalendar.Core.Store;

/// <summary>
/// Expands an item into the instances that fall inside a window (local-store-offline R3, recurring-events R2).
/// CalDAV events are read from their iCalendar payload, so overrides, EXDATEs, floating and all-day times are
/// applied with the given local timezone. MS365 events are read from their Graph JSON (one instance each, as
/// calendarView lists occurrences). Tasks sit on their due date (tasks interim): a date-only due date fills that
/// day, a due time is a point in time. To Do due dates are read from the payload so they follow the Windows timezone.
/// </summary>
internal static class OccurrenceExpander
{
    /// <summary>Each instance with its original start (its RECURRENCE-ID; the start itself when not recurring).</summary>
    public static List<(DateTimeOffset Start, DateTimeOffset End, DateTimeOffset Recurrence)> Expand(
        Item item, AccountType accountType, DateTimeOffset windowStart, DateTimeOffset windowEnd, TimeZoneInfo zone)
    {
        if (accountType == AccountType.CalDAV && item.Kind == ItemKind.Event && TryLoad(item.RawPayload) is { } calendar)
        {
            return Expand(calendar, windowStart, windowEnd, zone);
        }
        if (accountType == AccountType.MS365 && item.Kind == ItemKind.Event && item.RemoteId is not null
            && Graph.GraphEventMapper.IsSeriesMaster(item.RawPayload))
        {
            // Drawn through its occurrences, which calendarView lists (sync-ms365 R6). A series not sent yet shows its
            // first occurrence until then.
            return [];
        }
        if (accountType == AccountType.MS365 && item.Kind == ItemKind.Event && Graph.GraphEventMapper.Times(item.RawPayload, zone) is { } t)
        {
            // All-day dates follow the Windows timezone (sync-ms365 R8).
            return t.End >= windowStart && t.Start < windowEnd ? [(t.Start, t.End, t.Start)] : [];
        }

        if (item.Kind == ItemKind.Task)
        {
            return Task(item, accountType, windowStart, windowEnd, zone);
        }

        var s = item.StartUtc ?? item.DueUtc;
        if (s is null)
        {
            return [];
        }
        var en = item.EndUtc ?? s.Value;
        return en >= windowStart && s.Value < windowEnd ? [(s.Value, en, s.Value)] : [];
    }

    private static List<(DateTimeOffset Start, DateTimeOffset End, DateTimeOffset Recurrence)> Task(
        Item item, AccountType accountType, DateTimeOffset windowStart, DateTimeOffset windowEnd, TimeZoneInfo zone)
    {
        DateTimeOffset start, end;
        if (accountType == AccountType.MS365)
        {
            if (Graph.GraphTaskMapper.DueDate(item.RawPayload) is not { } due)
            {
                return [];
            }
            start = Graph.GraphEventMapper.Local(due.ToDateTime(TimeOnly.MinValue), zone);
            end = Graph.GraphEventMapper.Local(due.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        }
        else if (item.DueUtc is { } d)
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(d, zone).DateTime);
            (start, end) = item.AllDay
                ? (d, Graph.GraphEventMapper.Local(day.AddDays(1).ToDateTime(TimeOnly.MinValue), zone))
                : (d, d);
        }
        else
        {
            return [];
        }
        return end >= windowStart && start < windowEnd ? [(start, end, start)] : [];
    }

    /// <summary>
    /// The instances of an iCalendar object that start in [windowStart, windowEnd). Cancelled instances of a series
    /// are left out; a cancelled event that does not repeat is kept, drawn faded (attendees-invitations R5).
    /// </summary>
    internal static List<(DateTimeOffset Start, DateTimeOffset End, DateTimeOffset Recurrence)> Expand(
        Calendar calendar, DateTimeOffset windowStart, DateTimeOffset windowEnd, TimeZoneInfo zone) =>
        calendar
            .GetOccurrences(ToCal(windowStart))
            .TakeWhileBefore(ToCal(windowEnd))
            .Where(o => !(o.Source is CalendarEvent e && string.Equals(e.Status, EventStatus.Cancelled, StringComparison.OrdinalIgnoreCase)
                && !Cancelled.IsCancelledSingle(e)))
            .Select(o =>
            {
                var start = ToUtc(o.Period.StartTime, zone);
                var end = o.Period.EffectiveEndTime is { } e ? ToUtc(e, zone) : start;
                var recurrence = o.Source is CalendarEvent { RecurrenceIdentifier: { } id } ? ToUtc(id.StartTime, zone) : start;
                return (start, end, recurrence);
            })
            .ToList();

    /// <summary>Null when the payload is not iCalendar; the caller falls back to the normalised columns.</summary>
    private static Calendar? TryLoad(string payload)
    {
        try
        {
            return CalDav.CustomTimeZones.Load(payload) is { Events.Count: > 0 } calendar ? calendar : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static CalDateTime ToCal(DateTimeOffset t) => new(t.UtcDateTime, "UTC");

    /// <summary>Floating and all-day values are wall-clock times in the Windows timezone (timezone-handling R6).</summary>
    internal static DateTimeOffset ToUtc(CalDateTime t, TimeZoneInfo zone)
    {
        if (!t.IsFloating)
        {
            return new DateTimeOffset(t.AsUtc, TimeSpan.Zero);
        }
        var wall = DateTime.SpecifyKind(t.Value, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall))
        {
            // Skipped by a DST jump: move past the gap.
            wall = wall.AddHours(1);
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, zone), TimeSpan.Zero);
    }
}
