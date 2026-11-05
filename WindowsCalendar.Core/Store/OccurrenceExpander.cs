using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace WindowsCalendar.Core.Store;

/// <summary>
/// Expands an item into the instances that fall inside a window (local-store-offline R3, recurring-events R2).
/// CalDAV events are read from their iCalendar payload, so overrides, EXDATEs, floating and all-day times are
/// applied with the given local timezone. Everything else (MS365, tasks) uses the item's normalised columns.
/// </summary>
internal static class OccurrenceExpander
{
    public static List<(DateTimeOffset Start, DateTimeOffset End)> Expand(
        Item item, AccountType accountType, DateTimeOffset windowStart, DateTimeOffset windowEnd, TimeZoneInfo zone)
    {
        if (accountType == AccountType.CalDAV && item.Kind == ItemKind.Event && TryLoad(item.RawPayload) is { } calendar)
        {
            return calendar
                .GetOccurrences(ToCal(windowStart))
                .TakeWhileBefore(ToCal(windowEnd))
                .Where(o => !(o.Source is CalendarEvent e && string.Equals(e.Status, EventStatus.Cancelled, StringComparison.OrdinalIgnoreCase)))
                .Select(o =>
                {
                    var start = ToUtc(o.Period.StartTime, zone);
                    var end = o.Period.EffectiveEndTime is { } e ? ToUtc(e, zone) : start;
                    return (start, end);
                })
                .ToList();
        }

        var s = item.StartUtc ?? item.DueUtc;
        if (s is null)
        {
            return [];
        }
        var en = item.EndUtc ?? s.Value;
        return en >= windowStart && s.Value < windowEnd ? [(s.Value, en)] : [];
    }

    /// <summary>Null when the payload is not iCalendar; the caller falls back to the normalised columns.</summary>
    private static Calendar? TryLoad(string payload)
    {
        try
        {
            return Calendar.Load(payload) is { Events.Count: > 0 } calendar ? calendar : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static CalDateTime ToCal(DateTimeOffset t) => new(t.UtcDateTime, "UTC");

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
