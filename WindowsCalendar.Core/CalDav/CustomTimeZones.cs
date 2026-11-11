using System.Collections.Concurrent;
using Ical.Net;
using Ical.Net.CalendarComponents;
using NodaTime;
using NodaTime.TimeZones;

namespace WindowsCalendar.Core.CalDav;

/// <summary>
/// Custom VTIMEZONE definitions (timezone-handling R3, F5). Ical.Net resolves a TZID by name only and throws on one it
/// does not know (e.g. "Customized Time Zone"), so such an event could not be read or expanded. A calendar loaded here
/// registers each VTIMEZONE whose TZID is unknown, built from its STANDARD and DAYLIGHT rules, and Ical.Net's resolver
/// looks there first. The registry is process-wide and keyed by TZID: two different definitions under one TZID use the
/// first one seen. The VTIMEZONE stays in the payload as it is, so it round-trips unchanged.
/// </summary>
internal static class CustomTimeZones
{
    // Transitions are listed up to this year; the last offset holds after it.
    private const int LastYear = 2100;

    private static readonly ConcurrentDictionary<string, DateTimeZone> Zones = new();
    private static readonly Func<string, DateTimeZone> Default = TimeZoneResolvers.Default;

    static CustomTimeZones() => TimeZoneResolvers.TimeZoneResolver = id => Zones.TryGetValue(id, out var zone) ? zone : Default(id);

    /// <summary>Calendar.Load, with the calendar's custom timezones registered.</summary>
    public static Calendar? Load(string payload)
    {
        var calendar = Calendar.Load(payload);
        if (calendar is not null)
        {
            Register(calendar);
        }
        return calendar;
    }

    /// <summary>Whether Ical.Net can resolve the TZID: a zone it knows, or a registered custom one.</summary>
    public static bool IsKnown(string tzId)
    {
        try
        {
            TimeZoneResolvers.TimeZoneResolver(tzId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void Register(Calendar calendar)
    {
        foreach (var definition in calendar.TimeZones)
        {
            if (definition.TzId is { } id && !IsKnown(id) && Build(id, definition) is { } zone)
            {
                Zones.TryAdd(id, zone);
            }
        }
    }

    /// <summary>The zone from the definition's transitions; null when it has none that can be read.</summary>
    private static DateTimeZone? Build(string id, VTimeZone definition)
    {
        var transitions = new List<(Instant At, Offset From, Offset To, Offset Savings, string Name)>();
        try
        {
            foreach (var info in definition.TimeZoneInfos)
            {
                if (info.DtStart is not { } start || info.OffsetFrom is not { } offsetFrom || info.OffsetTo is not { } offsetTo)
                {
                    continue;
                }
                var from = Offset.FromTimeSpan(offsetFrom.Offset);
                var to = Offset.FromTimeSpan(offsetTo.Offset);
                var savings = string.Equals(info.Name, "DAYLIGHT", StringComparison.OrdinalIgnoreCase) && to > from ? to - from : Offset.Zero;
                // Each onset is a wall time in the offset in force before it.
                foreach (var o in info.GetOccurrences(start, null).TakeWhile(o => o.Period.StartTime.Year <= LastYear))
                {
                    var at = DateTime.SpecifyKind(o.Period.StartTime.Value - offsetFrom.Offset, DateTimeKind.Utc);
                    transitions.Add((Instant.FromDateTimeUtc(at), from, to, savings, info.TimeZoneName ?? info.Name ?? id));
                }
            }
        }
        catch (Exception)
        {
            return null;
        }
        if (transitions.Count == 0)
        {
            return null;
        }
        transitions.Sort((a, b) => a.At.CompareTo(b.At));
        var first = transitions[0];
        var intervals = new List<ZoneInterval> { new(first.Name, null, first.At, first.From, Offset.Zero) };
        for (var i = 0; i < transitions.Count; i++)
        {
            var t = transitions[i];
            if (i + 1 < transitions.Count && transitions[i + 1].At == t.At)
            {
                continue;
            }
            Instant? end = i + 1 < transitions.Count ? transitions[i + 1].At : null;
            intervals.Add(new ZoneInterval(t.Name, t.At, end, t.To, t.Savings));
        }
        return new Zone(id, [.. intervals]);
    }

    /// <summary>A zone from contiguous intervals; the first has no start, the last no end.</summary>
    private sealed class Zone(string id, ZoneInterval[] intervals)
        : DateTimeZone(id, false, intervals.Min(i => i.WallOffset), intervals.Max(i => i.WallOffset))
    {
        public override ZoneInterval GetZoneInterval(Instant instant)
        {
            // The last interval starting at or before the instant.
            int lo = 0, hi = intervals.Length - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (intervals[mid].Start <= instant)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return intervals[lo];
        }
    }
}
