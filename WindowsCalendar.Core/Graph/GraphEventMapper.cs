using System.Globalization;
using System.Text.Json;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Graph;

/// <summary>
/// Graph events to store items (sync-ms365 R12, slice 1). The JSON is kept as-is; the columns are derived from it.
/// Timed events are read as UTC (R8); all-day events are dates in the Windows timezone.
/// </summary>
public static class GraphEventMapper
{
    /// <summary>
    /// The item for an event from calendarView, or null when it is not shown: cancelled, a series master (calendarView
    /// returns its occurrences), or without usable times.
    /// </summary>
    public static Item? Map(JsonElement ev, string collectionId, TimeZoneInfo zone)
    {
        if (GraphClient.Text(ev, "id") is not { } id
            || (ev.TryGetProperty("isCancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True)
            || GraphClient.Text(ev, "type") == "seriesMaster"
            || Times(ev, zone) is not { } times)
        {
            return null;
        }
        var type = GraphClient.Text(ev, "type");
        var location = ev.TryGetProperty("location", out var l) ? GraphClient.Text(l, "displayName") : null;
        var modified = GraphClient.Text(ev, "lastModifiedDateTime") is { } m
            && DateTimeOffset.TryParse(m, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lm) ? lm : (DateTimeOffset?)null;
        return new Item(
            Guid.NewGuid().ToString("N"),
            collectionId,
            ItemKind.Event,
            id,
            ev.GetRawText(),
            Summary: GraphClient.Text(ev, "subject"),
            StartUtc: times.Start,
            EndUtc: times.End,
            AllDay: times.AllDay,
            TimeZone: GraphClient.Text(ev, "originalStartTimeZone"),
            Location: string.IsNullOrWhiteSpace(location) ? null : location,
            Status: GraphClient.Text(ev, "showAs"),
            IsRecurring: type is "occurrence" or "exception",
            Etag: GraphClient.Text(ev, "@odata.etag"),
            LastModifiedUtc: modified);
    }

    /// <summary>
    /// The editor's fields for a stored event, for view-only display (slice 1): times in <paramref name="zone"/>, all-day
    /// ones as dates with End exclusive, and the plain-text body preview as the description.
    /// </summary>
    public static CalDav.EventFields Read(Item item, TimeZoneInfo zone)
    {
        using var doc = JsonDocument.Parse(item.RawPayload);
        var ev = doc.RootElement;
        var (start, end, allDay) = Times(ev, zone) ?? (item.StartUtc ?? default, item.EndUtc ?? default, item.AllDay);
        var startWall = TimeZoneInfo.ConvertTime(start, zone).DateTime;
        var endWall = TimeZoneInfo.ConvertTime(end, zone).DateTime;
        return new CalDav.EventFields(GraphClient.Text(ev, "subject") ?? "", allDay,
            allDay ? startWall.Date : startWall, allDay ? endWall.Date : endWall, item.Location, GraphClient.Text(ev, "bodyPreview"));
    }

    /// <summary>The event's times from a stored payload, for the occurrence index; null when it can't be read.</summary>
    internal static (DateTimeOffset Start, DateTimeOffset End, bool AllDay)? Times(string payload, TimeZoneInfo zone)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return Times(doc.RootElement, zone);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (DateTimeOffset Start, DateTimeOffset End, bool AllDay)? Times(JsonElement ev, TimeZoneInfo zone)
    {
        if (!ev.TryGetProperty("start", out var s) || !ev.TryGetProperty("end", out var e)
            || Wall(s) is not { } start || Wall(e) is not { } end)
        {
            return null;
        }
        var allDay = ev.TryGetProperty("isAllDay", out var a) && a.ValueKind == JsonValueKind.True;
        if (allDay)
        {
            // A date: midnight to midnight in the Windows timezone.
            return (Local(start.Wall.Date, zone), Local(end.Wall.Date, zone), true);
        }
        return (Utc(start, zone), Utc(end, zone), false);
    }

    /// <summary>A dateTimeTimeZone: the wall time and its zone id ("UTC" with the UTC preference).</summary>
    private static (DateTime Wall, string? Zone)? Wall(JsonElement dateTimeTimeZone) =>
        GraphClient.Text(dateTimeTimeZone, "dateTime") is { } text
        && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall)
            ? (DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), GraphClient.Text(dateTimeTimeZone, "timeZone"))
            : null;

    private static DateTimeOffset Utc((DateTime Wall, string? Zone) t, TimeZoneInfo zone)
    {
        if (t.Zone is null or "UTC" or "Etc/UTC")
        {
            return new DateTimeOffset(t.Wall, TimeSpan.Zero);
        }
        // Graph uses Windows zone ids; unknown ones fall back to the Windows timezone.
        return Local(t.Wall, TimeZoneInfo.TryFindSystemTimeZoneById(t.Zone, out var own) ? own : zone);
    }

    private static DateTimeOffset Local(DateTime wall, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(wall))
        {
            wall = wall.AddHours(1);
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, zone), TimeSpan.Zero);
    }
}
