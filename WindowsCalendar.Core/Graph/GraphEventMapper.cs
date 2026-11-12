using System.Globalization;
using System.Text.Json;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Graph;

/// <summary>
/// Graph events to store items (sync-ms365 R6, R12). The JSON is kept as-is; the columns are derived from it.
/// Timed events are read as UTC (R8); all-day events are dates in the Windows timezone. Series masters are stored
/// (not drawn); occurrences record their master's id, and meetings are marked read-only.
/// </summary>
public static class GraphEventMapper
{
    /// <summary>
    /// The item for an event from calendarView delta or a write reply, or null when it has no usable times. Cancelled
    /// meetings are kept, drawn faded (attendees-invitations R5).
    /// </summary>
    public static Item? Map(JsonElement ev, string collectionId, TimeZoneInfo zone)
    {
        if (GraphClient.Text(ev, "id") is not { } id || Times(ev, zone) is null)
        {
            return null;
        }
        return Build(Guid.NewGuid().ToString("N"), collectionId, id, ev, zone) with { Etag = GraphClient.Text(ev, "@odata.etag") };
    }

    /// <summary>The item's columns re-derived from a changed payload (local edits); id, server id and etag are kept.</summary>
    internal static Item Rebuild(Item item, string payload, TimeZoneInfo zone)
    {
        using var doc = JsonDocument.Parse(payload);
        return Build(item.Id, item.CollectionId, item.RemoteId, doc.RootElement, zone) with { Etag = item.Etag };
    }

    private static Item Build(string itemId, string collectionId, string? remoteId, JsonElement ev, TimeZoneInfo zone)
    {
        var times = Times(ev, zone);
        var type = GraphClient.Text(ev, "type");
        var location = Location(ev);
        var modified = GraphClient.Text(ev, "lastModifiedDateTime") is { } m
            && DateTimeOffset.TryParse(m, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lm) ? lm : (DateTimeOffset?)null;
        return new Item(
            itemId,
            collectionId,
            ItemKind.Event,
            remoteId,
            ev.GetRawText(),
            Summary: GraphClient.Text(ev, "subject"),
            StartUtc: times?.Start,
            EndUtc: times?.End,
            AllDay: times?.AllDay ?? false,
            TimeZone: GraphClient.Text(ev, "originalStartTimeZone") ?? (ev.TryGetProperty("start", out var s) ? GraphClient.Text(s, "timeZone") : null),
            Location: location,
            Status: GraphClient.Text(ev, "showAs"),
            IsRecurring: type is "occurrence" or "exception" or "seriesMaster" || ev.TryGetProperty("recurrence", out var r) && r.ValueKind == JsonValueKind.Object,
            LastModifiedUtc: modified,
            SeriesId: type is "occurrence" or "exception" ? GraphClient.Text(ev, "seriesMasterId") : null,
            ReadOnly: IsMeeting(ev),
            Unaccepted: IsUnaccepted(ev),
            JoinUrl: JoinLink.FromGraph(ev)?.Url);
    }

    /// <summary>
    /// A meeting: someone else organised it, or it has an attendee other than the organiser (attendees-invitations R2).
    /// The app does not change meetings.
    /// </summary>
    internal static bool IsMeeting(JsonElement ev)
    {
        if (ev.TryGetProperty("isOrganizer", out var o) && o.ValueKind == JsonValueKind.False)
        {
            return true;
        }
        if (!ev.TryGetProperty("attendees", out var a) || a.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        var organiser = ev.TryGetProperty("organizer", out var org) ? Address(org) : null;
        return a.EnumerateArray().Any(attendee =>
            organiser is null || !string.Equals(Address(attendee), organiser, StringComparison.OrdinalIgnoreCase));

        static string? Address(JsonElement recipient) =>
            recipient.ValueKind == JsonValueKind.Object && recipient.TryGetProperty("emailAddress", out var e)
                ? GraphClient.Text(e, "address") : null;
    }

    /// <summary>Whether a stored payload is a meeting (for the one-off re-read of stored events).</summary>
    internal static bool IsMeeting(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return IsMeeting(doc.RootElement);
    }

    /// <summary>
    /// The event's Outlook link (attendees-invitations R6): its <c>webLink</c> when it is HTTPS on an Outlook host,
    /// else null.
    /// </summary>
    public static Uri? OutlookLink(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return GraphClient.Text(doc.RootElement, "webLink") is { } text
                && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                && uri.Host.ToLowerInvariant() is "outlook.office365.com" or "outlook.office.com" or "outlook.live.com"
                ? uri : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// An invitation you have not accepted: you are not the organiser, and your response is none, not responded or
    /// tentative (attendees-invitations interim).
    /// </summary>
    internal static bool IsUnaccepted(JsonElement ev) =>
        ev.TryGetProperty("isOrganizer", out var o) && o.ValueKind == JsonValueKind.False
        && ev.TryGetProperty("responseStatus", out var r)
        && GraphClient.Text(r, "response") is "none" or "notResponded" or "tentativelyAccepted";

    /// <summary>
    /// An invitation you have not answered at all (attendees-invitations R3): not the organiser, and the response is
    /// none or not responded. Tentative counts as answered.
    /// </summary>
    public static bool NeedsResponse(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var ev = doc.RootElement;
            return ev.TryGetProperty("isOrganizer", out var o) && o.ValueKind == JsonValueKind.False
                && ev.TryGetProperty("responseStatus", out var r) && GraphClient.Text(r, "response") is "none" or "notResponded";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether the stored payload is a series master (not drawn; its occurrences are).</summary>
    internal static bool IsSeriesMaster(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return GraphClient.Text(doc.RootElement, "type") == "seriesMaster"
                || doc.RootElement.TryGetProperty("recurrence", out var r) && r.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The editor's fields for a stored event: times in <paramref name="zone"/>, all-day ones as dates with End
    /// exclusive. A stub occurrence takes its title, location and description from <paramref name="master"/>.
    /// </summary>
    public static CalDav.EventFields Read(Item item, Item? master, TimeZoneInfo zone)
    {
        using var doc = JsonDocument.Parse(item.RawPayload);
        using var masterDoc = master is null ? null : JsonDocument.Parse(master.RawPayload);
        var ev = doc.RootElement;
        var (start, end, allDay) = Times(ev, zone) ?? (item.StartUtc ?? default, item.EndUtc ?? default, item.AllDay);
        var startWall = TimeZoneInfo.ConvertTime(start, zone).DateTime;
        var endWall = TimeZoneInfo.ConvertTime(end, zone).DateTime;
        var source = ev.TryGetProperty("subject", out _) || masterDoc is null ? ev : masterDoc.RootElement;
        var showAs = GraphClient.Text(ev, "showAs") ?? (masterDoc is null ? null : GraphClient.Text(masterDoc.RootElement, "showAs"));
        // An occurrence without its own reminder fields has its series' (reminders-notifications R1).
        var reminderSource = ev.TryGetProperty("isReminderOn", out _) || masterDoc is null ? ev : masterDoc.RootElement;
        return new CalDav.EventFields(GraphClient.Text(source, "subject") ?? "", allDay,
            allDay ? startWall.Date : startWall, allDay ? endWall.Date : endWall, Location(source), Description(source),
            ShowAsValues.FromGraph(showAs), Reminder(reminderSource));
    }

    /// <summary>The event's one reminder (reminders-notifications R5a): minutes before the start when on, else none.</summary>
    internal static Reminders.ReminderChoice Reminder(JsonElement ev) =>
        ev.TryGetProperty("isReminderOn", out var on) && on.ValueKind == JsonValueKind.True
        && ev.TryGetProperty("reminderMinutesBeforeStart", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var minutes)
            ? Reminders.ReminderChoice.Before(minutes)
            : Reminders.ReminderChoice.None;

    /// <summary>
    /// The location text: displayName, else the structured address as "street, city state postal code, country"
    /// (event-location R5).
    /// </summary>
    internal static string? Location(JsonElement ev)
    {
        if (!ev.TryGetProperty("location", out var l) || l.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (GraphClient.Text(l, "displayName") is { } name && !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }
        if (!l.TryGetProperty("address", out var a) || a.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        static string Join(string separator, params string?[] parts) =>
            string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
        var text = Join(", ", GraphClient.Text(a, "street"),
            Join(" ", GraphClient.Text(a, "city"), GraphClient.Text(a, "state"), GraphClient.Text(a, "postalCode")),
            GraphClient.Text(a, "countryOrRegion"));
        return text.Length == 0 ? null : text;
    }

    /// <summary>The body as plain text (reads ask for text); an HTML body stored earlier falls back to its preview.</summary>
    internal static string? Description(JsonElement ev)
    {
        var text = ev.TryGetProperty("body", out var b) && GraphClient.Text(b, "contentType") is not "html"
            ? GraphClient.Text(b, "content")
            : GraphClient.Text(ev, "bodyPreview");
        return string.IsNullOrEmpty(text) ? null : text.Replace("\r\n", "\n");
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

    internal static (DateTimeOffset Start, DateTimeOffset End, bool AllDay)? Times(JsonElement ev, TimeZoneInfo zone)
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

    internal static DateTimeOffset Local(DateTime wall, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(wall))
        {
            wall = wall.AddHours(1);
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, zone), TimeSpan.Zero);
    }
}
