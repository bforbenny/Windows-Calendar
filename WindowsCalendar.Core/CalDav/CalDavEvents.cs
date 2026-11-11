using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using WindowsCalendar.Core.Reminders;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.CalDav;

/// <summary>
/// The editor's fields (calendar-views R6, interim). For all-day events Start and End are dates, with End exclusive.
/// Reminder is the first reminder (reminders-notifications R5a); null leaves the event's reminders as they are (none on
/// a new event). MoreReminders counts the other alarms, which edits keep.
/// <para>Timezones (timezone-handling R7): with <see cref="StartZone"/> null, Start and End are wall-clock times in the
/// Windows timezone and a write keeps the event's own zones (floating, a custom zone, a separate end zone); a new
/// event gets the Windows timezone. With <see cref="StartZone"/> set, Start is a wall-clock time in it and End one in
/// <see cref="EndZone"/> (StartZone when null), and a write puts the event in those zones. Reads leave both null.
/// Zones are ignored for all-day events.</para>
/// </summary>
public sealed record EventFields(string Title, bool AllDay, DateTime Start, DateTime End, string? Location, string? Description,
    ShowAs ShowAs = ShowAs.Busy, ReminderChoice? Reminder = null, int MoreReminders = 0,
    TimeZoneInfo? StartZone = null, TimeZoneInfo? EndZone = null)
{
    /// <summary>Whether a write moves the event into the zones given (timezone-handling R7).</summary>
    public bool ZoneSet => StartZone is not null && !AllDay;

    /// <summary>The end's zone when <see cref="ZoneSet"/>.</summary>
    public TimeZoneInfo? EffectiveEndZone => EndZone ?? StartZone;

    /// <summary>The same times as wall-clock times in <paramref name="zone"/>, with no zones.</summary>
    public EventFields Walls(TimeZoneInfo zone) => ZoneSet
        ? this with { Start = Convert(Start, StartZone!, zone), End = Convert(End, EffectiveEndZone!, zone), StartZone = null, EndZone = null }
        : this with { StartZone = null, EndZone = null };

    /// <summary>A wall-clock time in one zone as one in another; a time skipped by a DST change moves an hour on.</summary>
    public static DateTime Convert(DateTime wall, TimeZoneInfo from, TimeZoneInfo to) =>
        from.Id == to.Id ? wall : DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(CalDavEvents.ToUtc(wall, from), to), DateTimeKind.Unspecified);
}

/// <summary>
/// Creates and edits CalDAV events for the editor. Edits change the stored iCalendar object in place and touch only
/// the fields that changed, so unknown properties, alarms and timezone definitions survive (sync-radicale R8).
/// </summary>
public static class CalDavEvents
{
    /// <summary>The editor's fields for an event's master component.</summary>
    public static EventFields Read(Item item, TimeZoneInfo zone) => Read(Master(CustomTimeZones.Load(item.RawPayload)!), zone);

    /// <summary>
    /// A new event in the Windows timezone, or the zones in <paramref name="fields"/> (timezone-handling R7), with a
    /// UUID-based UID and href (sync-radicale R7).
    /// </summary>
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
        var calendar = CustomTimeZones.Load(item.RawPayload)!;
        var ev = Master(calendar);
        var old = Read(ev, zone);
        var walls = fields.Walls(zone);
        var timesChanged = old.AllDay != fields.AllDay || old.Start != walls.Start || old.End != walls.End || fields.ZoneSet;
        var textChanged = old.Title != fields.Title || Blank(old.Location) != Blank(fields.Location)
            || Blank(old.Description) != Blank(fields.Description) || old.ShowAs != fields.ShowAs || ReminderChanged(old, fields);
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
            SetText(ev, fields, old);
        }
        ev.Sequence += 1;
        ev.DtStamp = Utc(now);
        ev.LastModified = Utc(now);
        return ToItem(item.Id, item.CollectionId, item.RemoteId!, item.Etag, calendar, zone);
    }

    /// <summary>The same event in another collection, as a new item with its own id and href (calendar-views R8).</summary>
    public static Item MoveTo(Item item, Collection target)
    {
        var uid = Master(CustomTimeZones.Load(item.RawPayload)!).Uid!;
        return item with { Id = Guid.NewGuid().ToString("N"), CollectionId = target.Id, RemoteId = Href(target, uid), Etag = null };
    }

    /// <summary>
    /// A new, independent copy of an event in <paramref name="target"/> (duplicate-event R3-R5): new UID and href, no
    /// attendees, organiser, conference or join link. An occurrence (<paramref name="recurrence"/>) becomes a single event
    /// with that occurrence's fields; otherwise the whole event is copied, repeat rule and overrides included.
    /// <paramref name="at"/> moves the copy (a Ctrl+drag drop).
    /// </summary>
    public static Item Duplicate(Item item, DateTimeOffset? recurrence, Collection target, TimeZoneInfo zone, DateTimeOffset now,
        (bool AllDay, DateTime Start, DateTime End)? at = null)
    {
        var calendar = CustomTimeZones.Load(item.RawPayload)!;
        var events = calendar.Events.ToList();
        var times = at;
        if (recurrence is { } occurrence)
        {
            var fields = CalDavRecurrence.ReadOccurrence(item, occurrence, zone);
            times ??= (fields.AllDay, fields.Start, fields.End);
            var copy = (CalDavRecurrence.Override(calendar, occurrence, zone) ?? Master(calendar)).Copy<CalendarEvent>()!;
            copy.RecurrenceIdentifier = null;
            copy.RecurrenceRule = null;
            copy.RecurrenceDates.Clear();
            copy.ExceptionDates.Clear();
            foreach (var e in events)
            {
                calendar.Children.Remove(e);
            }
            calendar.Events.Add(copy);
            events = [copy];
        }
        var uid = Guid.NewGuid().ToString();
        foreach (var ev in events)
        {
            ev.Uid = uid;
            foreach (var name in new[] { "ATTENDEE", "ORGANIZER", "CONFERENCE", "LAST-MODIFIED" })
            {
                ev.Properties.Remove(name);
            }
            // The meeting's join link stays with the meeting (online-meeting-join R7); other URLs are kept.
            foreach (var name in JoinLink.PropertyNames.Append("URL"))
            {
                if (JoinLink.Recognise(ev.Properties.Get<object>(name)?.ToString()) is not null)
                {
                    ev.Properties.Remove(name);
                }
            }
            ev.Created = Utc(now);
            ev.DtStamp = Utc(now);
            ev.Sequence = 0;
        }
        if (times is { } t)
        {
            var master = Master(calendar);
            SetTimes(calendar, master, Read(master, zone) with { AllDay = t.AllDay, Start = t.Start, End = t.End }, zone);
        }
        return ToItem(Guid.NewGuid().ToString("N"), target.Id, Href(target, uid), null, calendar, zone);
    }

    /// <summary>The resource URL for a UID in a collection: <c>&lt;collection&gt;/&lt;uid&gt;.ics</c> (sync-radicale R7).</summary>
    public static string Href(Collection collection, string uid) =>
        new Uri(new Uri(collection.RemoteRef.TrimEnd('/') + "/"), Uri.EscapeDataString(uid) + ".ics").AbsoluteUri;

    internal static CalendarEvent Master(Calendar calendar) =>
        calendar.Events.FirstOrDefault(e => e.RecurrenceIdentifier is null) ?? calendar.Events.First();

    internal static EventFields Read(CalendarEvent ev, TimeZoneInfo zone)
    {
        var start = ev.DtStart!;
        if (!start.HasTime)
        {
            var endDate = ev.DtEnd is { } e ? e.Value.Date : start.Value.Date.AddDays(1);
            return new EventFields(ev.Summary ?? "", true, start.Value.Date, endDate, ev.Location, ev.Description, ReadShowAs(ev),
                ReadReminder(ev, zone), MoreReminders(ev));
        }
        var end = ev.DtEnd ?? start.Add(ev.EffectiveDuration);
        return new EventFields(ev.Summary ?? "", false, Wall(start, zone), Wall(end, zone), ev.Location, ev.Description, ReadShowAs(ev),
            ReadReminder(ev, zone), MoreReminders(ev));
    }

    // ---- Reminder (reminders-notifications R5a): the first DISPLAY alarm; other alarms are kept ----

    private static Alarm? FirstDisplay(CalendarEvent ev) =>
        ev.Alarms.FirstOrDefault(a => string.Equals(a.Action, "DISPLAY", StringComparison.OrdinalIgnoreCase) && a.Trigger is not null);

    private static int MoreReminders(CalendarEvent ev) => ev.Alarms.Count - (FirstDisplay(ev) is null ? 0 : 1);

    /// <summary>Minutes before the start; a fixed time or a trigger relative to the end is kept as it is.</summary>
    private static ReminderChoice ReadReminder(CalendarEvent ev, TimeZoneInfo zone)
    {
        if (FirstDisplay(ev)?.Trigger is not { } trigger)
        {
            return ReminderChoice.None;
        }
        if (trigger.IsRelative && trigger.Duration is { } duration)
        {
            var minutes = (int)Math.Round(-duration.ToTimeSpanUnspecified().TotalMinutes);
            return string.Equals(trigger.Related, "END", StringComparison.OrdinalIgnoreCase)
                ? new ReminderChoice(null, minutes switch
                {
                    0 => "At end",
                    > 0 => $"{minutes} min before end",
                    _ => $"{-minutes} min after end",
                })
                : ReminderChoice.Before(minutes);
        }
        return trigger.DateTime is { } at
            ? new ReminderChoice(null, $"At {TimeZoneInfo.ConvertTime(OccurrenceExpander.ToUtc(at, zone), zone):ddd d MMM HH:mm}")
            : ReminderChoice.None;
    }

    internal static bool ReminderChanged(EventFields old, EventFields fields) =>
        fields.Reminder is { } reminder && reminder != old.Reminder;

    /// <summary>
    /// Sets the first DISPLAY alarm's trigger, adds one when there is none, or removes it for None. A kept reminder is
    /// not written. A whole number of days or hours is written in that unit.
    /// </summary>
    private static void WriteReminder(CalendarEvent ev, ReminderChoice reminder)
    {
        if (reminder.Kept is not null)
        {
            return;
        }
        var first = FirstDisplay(ev);
        if (reminder.MinutesBefore is not { } m)
        {
            if (first is not null)
            {
                ev.Children.Remove(first);
            }
            return;
        }
        var offset = m % 1440 == 0 ? Duration.FromDays(-m / 1440) : m % 60 == 0 ? Duration.FromHours(-m / 60) : Duration.FromMinutes(-m);
        if (first is null)
        {
            ev.Alarms.Add(new Alarm { Action = "DISPLAY", Description = "Reminder", Trigger = new Trigger(offset) });
        }
        else
        {
            first.Trigger = new Trigger(offset);
        }
    }

    private const string BusyStatus = "X-MICROSOFT-CDO-BUSYSTATUS";

    /// <summary>
    /// X-MICROSOFT-CDO-BUSYSTATUS when it holds a known value, else TRANSP: TRANSPARENT is Free, anything else Busy
    /// (event-show-as R1, R3).
    /// </summary>
    internal static ShowAs ReadShowAs(CalendarEvent ev) =>
        ev.Properties.Get<string>(BusyStatus)?.Trim().ToUpperInvariant() switch
        {
            "BUSY" => ShowAs.Busy,
            "FREE" => ShowAs.Free,
            "TENTATIVE" => ShowAs.Tentative,
            "OOF" => ShowAs.OutOfOffice,
            "WORKINGELSEWHERE" => ShowAs.WorkingElsewhere,
            _ => "TRANSPARENT".Equals(ev.Transparency, StringComparison.OrdinalIgnoreCase) ? ShowAs.Free : ShowAs.Busy,
        };

    /// <summary>
    /// TRANSP (TRANSPARENT for Free, OPAQUE otherwise) and, when the event already carries it or the value is neither
    /// Busy nor Free, X-MICROSOFT-CDO-BUSYSTATUS to match (event-show-as R3).
    /// </summary>
    private static void WriteShowAs(CalendarEvent ev, ShowAs value)
    {
        ev.Transparency = value == ShowAs.Free ? "TRANSPARENT" : "OPAQUE";
        var had = ev.Properties.ContainsKey(BusyStatus);
        ev.Properties.Remove(BusyStatus);
        if (had || value is not (ShowAs.Busy or ShowAs.Free))
        {
            ev.Properties.Add(new CalendarProperty(BusyStatus, value switch
            {
                ShowAs.Free => "FREE",
                ShowAs.Tentative => "TENTATIVE",
                ShowAs.OutOfOffice => "OOF",
                ShowAs.WorkingElsewhere => "WORKINGELSEWHERE",
                _ => "BUSY",
            }));
        }
    }

    internal static DateTime Wall(CalDateTime t, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(OccurrenceExpander.ToUtc(t, zone), zone).DateTime;

    /// <summary>Raised with the TZID when an event's own zone can't be resolved and its times are written in UTC (timezone-handling F1).</summary>
    public static event Action<string>? ZoneNotKept;

    /// <summary>
    /// All-day events get DATE values. Timed events keep their form: floating stays floating, UTC stays UTC, and
    /// a TZID event is written in its own zone, a custom VTIMEZONE in the calendar included (timezone-handling F1);
    /// a DTEND with its own TZID keeps it (F2). Only a TZID nothing resolves is written in UTC. An all-day event
    /// becoming timed gets the Windows timezone. Zones in <paramref name="fields"/> are written as IANA TZIDs with
    /// their VTIMEZONEs (timezone-handling R7).
    /// </summary>
    internal static void SetTimes(Calendar calendar, CalendarEvent ev, EventFields fields, TimeZoneInfo zone)
    {
        var oldEnd = ev.DtEnd;
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
        if (fields.ZoneSet)
        {
            ev.DtStart = InZone(calendar, ToUtc(fields.Start, fields.StartZone!), fields.StartZone!);
            ev.DtEnd = InZone(calendar, ToUtc(fields.End, fields.EffectiveEndZone!), fields.EffectiveEndZone!);
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
        if (tzId is null || current is { IsUtc: true } || !CustomTimeZones.IsKnown(tzId))
        {
            if (tzId is not null && current is { IsUtc: false })
            {
                ZoneNotKept?.Invoke(tzId);
            }
            ev.DtStart = new CalDateTime(ToUtc(fields.Start, zone), "UTC");
            ev.DtEnd = new CalDateTime(ToUtc(fields.End, zone), "UTC");
            return;
        }
        var endTzId = current is not null && oldEnd is { HasTime: true, IsFloating: false, IsUtc: false, TzId: { } e }
            && e != tzId && CustomTimeZones.IsKnown(e) ? e : tzId;
        ev.DtStart = InZone(ToUtc(fields.Start, zone), tzId);
        ev.DtEnd = InZone(ToUtc(fields.End, zone), endTzId);
        AddTimeZones(calendar, tzId, endTzId);
    }

    /// <summary>A VTIMEZONE for each TZID the calendar does not define yet.</summary>
    internal static void AddTimeZones(Calendar calendar, params string[] tzIds)
    {
        foreach (var id in tzIds.Distinct())
        {
            if (!calendar.TimeZones.Any(z => z.TzId == id))
            {
                calendar.AddTimeZone(VTimeZone.FromDateTimeZone(id));
            }
        }
    }

    /// <summary>A UTC instant as a wall time in the TZID, as Ical.Net resolves it (custom VTIMEZONEs included).</summary>
    internal static CalDateTime InZone(DateTime utc, string tzId) => new CalDateTime(utc, "UTC").ToTimeZone(tzId);

    /// <summary>
    /// Writes the text fields, show-as and reminder that differ from <paramref name="old"/> (all of them when null), so untouched properties
    /// keep their parameters. A changed location is written fresh, without its old parameters, GEO or
    /// X-APPLE-STRUCTURED-LOCATION (event-location R3).
    /// </summary>
    internal static void SetText(CalendarEvent ev, EventFields fields, EventFields? old = null)
    {
        if (old is null || old.Title != fields.Title)
        {
            ev.Summary = fields.Title;
        }
        if (old is null || Blank(old.Location) != Blank(fields.Location))
        {
            // Remove first, then add: in Ical.Net 5.2.4 setting ev.Location around a Properties.Remove can lose it.
            ev.Properties.Remove("LOCATION");
            ev.Properties.Remove("GEO");
            ev.Properties.Remove("X-APPLE-STRUCTURED-LOCATION");
            if (Blank(fields.Location) is { } location)
            {
                ev.Properties.Add(new CalendarProperty("LOCATION", location));
            }
        }
        if (old is null || Blank(old.Description) != Blank(fields.Description))
        {
            ev.Description = Blank(fields.Description);
        }
        if (old is null || old.ShowAs != fields.ShowAs)
        {
            WriteShowAs(ev, fields.ShowAs);
        }
        if (fields.Reminder is { } reminder && (old is null || ReminderChanged(old, fields)))
        {
            WriteReminder(ev, reminder);
        }
    }

    internal static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    internal static string? IanaId(TimeZoneInfo zone) =>
        zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null;

    /// <summary>
    /// A UTC instant in a chosen zone (timezone-handling R7): its IANA TZID, with a VTIMEZONE added; UTC for UTC or a
    /// zone with no IANA id.
    /// </summary>
    internal static CalDateTime InZone(Calendar calendar, DateTime utc, TimeZoneInfo zone)
    {
        if (zone.Id is "UTC" or "Etc/UTC" || IanaId(zone) is not { } id)
        {
            return new CalDateTime(utc, "UTC");
        }
        AddTimeZones(calendar, id);
        return InZone(utc, id);
    }

    internal static DateTime ToUtc(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall))
        {
            wall = wall.AddHours(1);
        }
        return TimeZoneInfo.ConvertTimeToUtc(wall, zone);
    }

    internal static CalDateTime Utc(DateTimeOffset t) => new(t.UtcDateTime, "UTC");

    /// <summary>The item with an earlier payload put back, e.g. for undo (undo R4).</summary>
    public static Item Restore(Item item, string payload, TimeZoneInfo zone) =>
        ToItem(item.Id, item.CollectionId, item.RemoteId!, item.Etag, CustomTimeZones.Load(payload)!, zone);

    internal static Item ToItem(string id, string collectionId, string href, string? etag, Calendar calendar, TimeZoneInfo zone)
    {
        KeepUriValues(calendar);
        var data = new CalendarSerializer().SerializeToString(calendar)!;
        return CalDavItemMapper.Map(href, etag ?? "", data, zone)! with { Id = id, CollectionId = collectionId, Etag = etag };
    }

    /// <summary>
    /// Ical.Net reads unknown properties marked VALUE=URI (e.g. X-APPLE-STRUCTURED-LOCATION) as text and would write
    /// them back with commas escaped; as Uri values they are written unchanged (event-location R4, sync-radicale R8).
    /// </summary>
    private static void KeepUriValues(ICalendarObject parent)
    {
        if (parent is ICalendarComponent component)
        {
            foreach (var p in component.Properties)
            {
                if (p.Value is string text && "URI".Equals(p.Parameters.Get("VALUE"), StringComparison.OrdinalIgnoreCase)
                    && Uri.TryCreate(text, UriKind.Absolute, out var uri))
                {
                    p.Value = uri;
                }
            }
        }
        foreach (var child in parent.Children)
        {
            KeepUriValues(child);
        }
    }
}
