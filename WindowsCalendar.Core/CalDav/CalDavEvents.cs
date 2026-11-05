using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.CalDav;

/// <summary>
/// The editor's fields (calendar-views R6, interim). Start and End are wall-clock times in the Windows timezone;
/// for all-day events they are dates, with End exclusive.
/// </summary>
public sealed record EventFields(string Title, bool AllDay, DateTime Start, DateTime End, string? Location, string? Description);

/// <summary>
/// Creates and edits CalDAV events for the editor. Edits change the stored iCalendar object in place and touch only
/// the fields that changed, so unknown properties, alarms and timezone definitions survive (sync-radicale R8).
/// </summary>
public static class CalDavEvents
{
    /// <summary>The editor's fields for an event's master component.</summary>
    public static EventFields Read(Item item, TimeZoneInfo zone) => Read(Master(Calendar.Load(item.RawPayload)!), zone);

    /// <summary>A new event in the Windows timezone, with a UUID-based UID and href (sync-radicale R7).</summary>
    public static Item Create(Collection collection, EventFields fields, TimeZoneInfo zone, DateTimeOffset now)
    {
        var uid = Guid.NewGuid().ToString();
        var calendar = new Calendar();
        var ev = new CalendarEvent
        {
            Uid = uid,
            Created = Utc(now),
            DtStamp = Utc(now),
            Sequence = 0,
        };
        calendar.Events.Add(ev);
        SetTimes(calendar, ev, fields, zone);
        SetText(ev, fields);
        return ToItem(Guid.NewGuid().ToString("N"), collection.Id, Href(collection, uid), null, calendar, zone);
    }

    /// <summary>
    /// The item with the changed fields written into its payload; SEQUENCE, DTSTAMP and LAST-MODIFIED move on.
    /// Null when no field changed.
    /// </summary>
    public static Item? Edit(Item item, EventFields fields, TimeZoneInfo zone, DateTimeOffset now)
    {
        var calendar = Calendar.Load(item.RawPayload)!;
        var ev = Master(calendar);
        var old = Read(ev, zone);
        var timesChanged = old.AllDay != fields.AllDay || old.Start != fields.Start || old.End != fields.End;
        var textChanged = old.Title != fields.Title || Blank(old.Location) != Blank(fields.Location)
            || Blank(old.Description) != Blank(fields.Description);
        if (!timesChanged && !textChanged)
        {
            return null;
        }
        if (timesChanged)
        {
            SetTimes(calendar, ev, fields, zone);
        }
        if (textChanged)
        {
            SetText(ev, fields);
        }
        ev.Sequence += 1;
        ev.DtStamp = Utc(now);
        ev.LastModified = Utc(now);
        return ToItem(item.Id, item.CollectionId, item.RemoteId!, item.Etag, calendar, zone);
    }

    /// <summary>The same event in another collection, as a new item with its own id and href (calendar-views R8).</summary>
    public static Item MoveTo(Item item, Collection target)
    {
        var uid = Master(Calendar.Load(item.RawPayload)!).Uid!;
        return item with { Id = Guid.NewGuid().ToString("N"), CollectionId = target.Id, RemoteId = Href(target, uid), Etag = null };
    }

    /// <summary>The resource URL for a UID in a collection: <c>&lt;collection&gt;/&lt;uid&gt;.ics</c> (sync-radicale R7).</summary>
    public static string Href(Collection collection, string uid) =>
        new Uri(new Uri(collection.RemoteRef.TrimEnd('/') + "/"), Uri.EscapeDataString(uid) + ".ics").AbsoluteUri;

    private static CalendarEvent Master(Calendar calendar) =>
        calendar.Events.FirstOrDefault(e => e.RecurrenceIdentifier is null) ?? calendar.Events.First();

    private static EventFields Read(CalendarEvent ev, TimeZoneInfo zone)
    {
        var start = ev.DtStart!;
        if (!start.HasTime)
        {
            var endDate = ev.DtEnd is { } e ? e.Value.Date : start.Value.Date.AddDays(1);
            return new EventFields(ev.Summary ?? "", true, start.Value.Date, endDate, ev.Location, ev.Description);
        }
        var end = ev.DtEnd ?? start.Add(ev.EffectiveDuration);
        return new EventFields(ev.Summary ?? "", false, Wall(start, zone), Wall(end, zone), ev.Location, ev.Description);
    }

    private static DateTime Wall(CalDateTime t, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(OccurrenceExpander.ToUtc(t, zone), zone).DateTime;

    /// <summary>
    /// All-day events get DATE values. Timed events keep their form: floating stays floating, UTC stays UTC, and
    /// a TZID event is written in its own zone. An all-day event becoming timed gets the Windows timezone.
    /// </summary>
    private static void SetTimes(Calendar calendar, CalendarEvent ev, EventFields fields, TimeZoneInfo zone)
    {
        if (ev.Duration is not null)
        {
            ev.Duration = null;
        }
        if (fields.AllDay)
        {
            ev.DtStart = new CalDateTime(DateOnly.FromDateTime(fields.Start));
            ev.DtEnd = new CalDateTime(DateOnly.FromDateTime(fields.End));
            return;
        }

        var current = ev.DtStart is { HasTime: true } s ? s : null;
        if (current is { IsFloating: true })
        {
            ev.DtStart = new CalDateTime(fields.Start, null);
            ev.DtEnd = new CalDateTime(fields.End, null);
            return;
        }

        var tzId = current is null ? IanaId(zone) : current.TzId;
        if (tzId is null || current is { IsUtc: true } || !TimeZoneInfo.TryFindSystemTimeZoneById(tzId, out var eventZone))
        {
            ev.DtStart = new CalDateTime(ToUtc(fields.Start, zone), "UTC");
            ev.DtEnd = new CalDateTime(ToUtc(fields.End, zone), "UTC");
            return;
        }
        ev.DtStart = new CalDateTime(TimeZoneInfo.ConvertTimeFromUtc(ToUtc(fields.Start, zone), eventZone), tzId);
        ev.DtEnd = new CalDateTime(TimeZoneInfo.ConvertTimeFromUtc(ToUtc(fields.End, zone), eventZone), tzId);
        if (!calendar.TimeZones.Any(z => z.TzId == tzId))
        {
            calendar.AddTimeZone(VTimeZone.FromDateTimeZone(tzId));
        }
    }

    private static void SetText(CalendarEvent ev, EventFields fields)
    {
        ev.Summary = fields.Title;
        ev.Location = Blank(fields.Location);
        ev.Description = Blank(fields.Description);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? IanaId(TimeZoneInfo zone) =>
        zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null;

    private static DateTime ToUtc(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall))
        {
            wall = wall.AddHours(1);
        }
        return TimeZoneInfo.ConvertTimeToUtc(wall, zone);
    }

    private static CalDateTime Utc(DateTimeOffset t) => new(t.UtcDateTime, "UTC");

    private static Item ToItem(string id, string collectionId, string href, string? etag, Calendar calendar, TimeZoneInfo zone)
    {
        var data = new CalendarSerializer().SerializeToString(calendar)!;
        return CalDavItemMapper.Map(href, etag ?? "", data, zone)! with { Id = id, CollectionId = collectionId, Etag = etag };
    }
}
