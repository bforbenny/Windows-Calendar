using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Views;

/// <summary>
/// Timezone names and the original-timezone time of an event (timezone-handling R8, R9). Zone ids are Windows ids
/// (Graph originalStartTimeZone) or IANA ids (CalDAV TZID); .NET resolves both. Custom and unknown zones, UTC (which
/// Graph reports for events with no zone of their own) and floating times give nothing.
/// </summary>
public static class TimeZoneLabels
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> Zones = new();

    /// <summary>The zone for a stored id; null when there is none, it is UTC, or Windows does not know it.</summary>
    public static TimeZoneInfo? Resolve(string? id) =>
        string.IsNullOrWhiteSpace(id) || id is "UTC" or "Etc/UTC" or "tzone://Microsoft/Utc"
            ? null
            : Zones.GetOrAdd(id, k => TimeZoneInfo.TryFindSystemTimeZoneById(k, out var zone) ? zone : null);

    /// <summary>Whether two zones are the same zone, whether named by a Windows or an IANA id.</summary>
    public static bool Same(TimeZoneInfo a, TimeZoneInfo b) => WindowsId(a) == WindowsId(b);

    /// <summary>
    /// A short name for the ruler and event details (R8 label): "UTC", else the IANA id's last segment ("Sydney",
    /// "New York"), else the display name without its "(UTC+hh:mm)" prefix, up to the first comma.
    /// </summary>
    public static string ShortName(TimeZoneInfo zone)
    {
        if (WindowsId(zone) == "UTC")
        {
            return "UTC";
        }
        var iana = zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var id) ? id : null;
        if (iana is not null && !iana.StartsWith("Etc/", StringComparison.Ordinal))
        {
            return iana[(iana.LastIndexOf('/') + 1)..].Replace('_', ' ');
        }
        var name = zone.DisplayName;
        var close = name.IndexOf(") ", StringComparison.Ordinal);
        if (name.StartsWith("(UTC", StringComparison.Ordinal) && close > 0)
        {
            name = name[(close + 2)..];
        }
        return name.Split(',')[0].Trim();
    }

    /// <summary>
    /// The event's times in its own zone when that differs from <paramref name="display"/> (R9): "09:00 – 10:00 New
    /// York time", or with a separate end zone "09:00 Sydney – 22:00 London". A time on another day than the
    /// displayed start gets its weekday. Null when the start zone is unknown or both zones are the display zone.
    /// </summary>
    public static string? OriginalTime(DateTimeOffset start, DateTimeOffset end, string? startZone, string? endZone,
        TimeZoneInfo display, CultureInfo? culture = null)
    {
        if (Resolve(startZone) is not { } from)
        {
            return null;
        }
        var to = Resolve(endZone) ?? from;
        if (Same(from, display) && Same(to, display))
        {
            return null;
        }
        culture ??= CultureInfo.CurrentCulture;
        var s = TimeZoneInfo.ConvertTime(start, from);
        var e = TimeZoneInfo.ConvertTime(end, to);
        var shownDay = TimeZoneInfo.ConvertTime(start, display).Date;
        string Time(DateTimeOffset t, DateTime day) => (t.Date != day ? t.ToString("ddd ", culture) : "") + t.ToString("t", culture);
        return Same(from, to)
            ? $"{Time(s, shownDay)} – {Time(e, s.Date)} {ShortName(from)} time"
            : $"{Time(s, shownDay)} {ShortName(from)} – {Time(e, s.Date)} {ShortName(to)}";
    }

    /// <summary>
    /// The start and end zone ids of an event as stored (R9): Graph originalStartTimeZone / originalEndTimeZone, or the
    /// CalDAV master's DTSTART / DTEND TZID. Null for a zone it does not have (floating, or no separate end).
    /// </summary>
    public static (string? Start, string? End) EventZones(Item item)
    {
        if (item.RawPayload.TrimStart().StartsWith('{'))
        {
            using var json = JsonDocument.Parse(item.RawPayload);
            string? Text(string name) => json.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Text("originalStartTimeZone") ?? item.TimeZone, Text("originalEndTimeZone"));
        }
        var ev = CalDavEvents.Master(CustomTimeZones.Load(item.RawPayload)!);
        return (ev.DtStart?.TzId, ev.DtEnd?.TzId);
    }

    /// <summary>
    /// The event's own zones for the editor's timezone picker (R7): Start is null for a floating event
    /// (<paramref name="Floating"/>) or a custom or unknown zone (kept); End is null when it is the start zone or can't
    /// be shown. An MS365 event with no zone of its own, or UTC, is in the Windows timezone, as its edits write it; a
    /// CalDAV UTC event is in UTC. All-day events give the Windows timezone. With <paramref name="recurrence"/>, a
    /// CalDAV occurrence's override gives its own zones (slice 4).
    /// </summary>
    public static (TimeZoneInfo? Start, TimeZoneInfo? End, bool Floating) OwnZones(Item item, TimeZoneInfo display,
        DateTimeOffset? recurrence = null)
    {
        if (item.AllDay)
        {
            return (display, null, false);
        }
        TimeZoneInfo? start, end;
        if (item.RawPayload.TrimStart().StartsWith('{'))
        {
            var ev = Graph.GraphEvents.Parse(item.RawPayload);
            var id = ev["originalStartTimeZone"]?.ToString() ?? ev["start"]?["timeZone"]?.ToString();
            if (!string.IsNullOrWhiteSpace(id) && id is not ("UTC" or "tzone://Microsoft/Utc") && Resolve(id) is null)
            {
                return (null, null, false);
            }
            start = Graph.GraphEvents.OwnZone(ev, display);
            end = Graph.GraphEvents.OwnEndZone(ev, display);
            // OwnEndZone gives UTC for a custom end zone, which can't be shown.
            end = ev["originalEndTimeZone"]?.ToString() is { } endId && Resolve(endId) is null ? null : end;
        }
        else
        {
            var calendar = CustomTimeZones.Load(item.RawPayload)!;
            var master = (recurrence is { } r ? CalDavRecurrence.Override(calendar, r, display) : null) ?? CalDavEvents.Master(calendar);
            if (master.DtStart is not { HasTime: true } s)
            {
                return (display, null, false);
            }
            if (s.IsFloating)
            {
                return (null, null, true);
            }
            static TimeZoneInfo? Zone(Ical.Net.DataTypes.CalDateTime t) => t.IsUtc || t.TzId is "UTC" or "Etc/UTC" ? TimeZoneInfo.Utc : Resolve(t.TzId);
            start = Zone(s);
            if (start is null)
            {
                return (null, null, false);
            }
            end = master.DtEnd is { HasTime: true, IsFloating: false } e ? Zone(e) : null;
        }
        return (start, end is null || Same(end, start) ? null : end, false);
    }

    /// <summary>The zone's Windows id (which Graph takes and the editor's picker lists), for a zone found by its IANA id too.</summary>
    public static string WindowsId(TimeZoneInfo zone) =>
        zone.HasIanaId && TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out var id) ? id : zone.Id;
}
