using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Sync;

/// <summary>The event as it will be in the other account, and what it cannot take along (shown before the move).</summary>
public sealed record TransferResult(Item Moved, IReadOnlyList<string> Lost);

/// <summary>
/// Moving an event to a calendar in another account (calendar-views R8). Between two accounts of the same kind the
/// stored event is copied whole. Between CalDAV and Microsoft 365 the fields are mapped: title, times in the event's
/// own timezone, all-day, location (with coordinates), description, show-as (event-show-as R1, R3), repeat rule, first
/// reminder, privacy, importance and categories. Meetings, invitations and repeating Microsoft 365 events are refused.
/// </summary>
public static partial class EventTransfer
{
    public static TransferResult Move(Item item, bool fromGraph, Collection target, bool toGraph, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (fromGraph)
        {
            var ev = GraphEvents.Parse(item.RawPayload);
            if (item.ReadOnly)
            {
                throw new EditRefusedException("Meetings and invitations can't be moved to another calendar.");
            }
            if (item.SeriesId is not null || ev["recurrence"] is JsonObject)
            {
                throw new EditRefusedException("Repeating Microsoft 365 events can't be moved to another account yet.");
            }
            return toGraph ? new(GraphEvents.MoveTo(item, target, zone), []) : GraphToCalDav(item, ev, target, zone, now);
        }
        var calendar = Calendar.Load(item.RawPayload)!;
        var master = CalDavEvents.Master(calendar);
        if (IsMeeting(master))
        {
            throw new EditRefusedException("Meetings and invitations can't be moved to another calendar.");
        }
        return toGraph ? CalDavToGraph(item, calendar, master, target, zone) : new(CalDavEvents.MoveTo(item, target), []);
    }

    // ---- CalDAV to Microsoft 365 ----

    private static TransferResult CalDavToGraph(Item item, Calendar calendar, CalendarEvent master, Collection target, TimeZoneInfo zone)
    {
        var lost = new List<string>();
        var rule = CalDavRecurrence.ReadRule(item, zone)
            ?? throw new EditRefusedException("This repeat pattern can't be moved to Microsoft 365.");
        var fields = CalDavEvents.Read(master, zone);
        var created = GraphEvents.Create(target, fields, zone);
        var ev = GraphEvents.Parse(created.RawPayload);

        // Times in the event's own timezone, when Windows knows it.
        var own = !fields.AllDay && master.DtStart is { TzId: { } tzId } && WindowsZone(tzId) is { } z ? z : zone;
        GraphEvents.SetTimes(ev, fields, zone, own);
        if (master.GeographicLocation is { } geo && ev["location"] is JsonObject location)
        {
            location["coordinates"] = new JsonObject { ["latitude"] = geo.Latitude, ["longitude"] = geo.Longitude };
        }

        var alarms = master.Alarms.ToList();
        var reminder = alarms.Select(BeforeStart).FirstOrDefault(m => m is not null);
        ev["isReminderOn"] = reminder is not null;
        if (reminder is { } minutes)
        {
            ev["reminderMinutesBeforeStart"] = minutes;
        }
        var others = alarms.Count - (reminder is null ? 0 : 1);
        if (others > 0)
        {
            lost.Add(others == 1 ? "1 other reminder" : $"{others} other reminders");
        }

        switch (master.Class?.ToUpperInvariant())
        {
            case "PRIVATE":
                ev["sensitivity"] = "private";
                break;
            case "CONFIDENTIAL":
                ev["sensitivity"] = "confidential";
                break;
        }
        if (master.Priority is >= 1 and <= 4)
        {
            ev["importance"] = "high";
        }
        else if (master.Priority is >= 6 and <= 9)
        {
            ev["importance"] = "low";
        }
        if (master.Categories.Count > 0)
        {
            ev["categories"] = new JsonArray(master.Categories.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
        }

        var moved = GraphEventMapper.Rebuild(created, ev.ToJsonString(), zone);
        if (rule.Frequency != RepeatFrequency.None)
        {
            // The rule is written in the event's own timezone too; the new event has no originalStartTimeZone yet.
            if (GraphRecurrence.ChangeRule(moved, null, false, rule, target, [], own)?.Updated is { } repeating)
            {
                moved = GraphEventMapper.Rebuild(repeating, repeating.RawPayload, zone);
            }
            var overrides = calendar.Events.Count(e => e.RecurrenceIdentifier is not null);
            var deleted = master.ExceptionDates.GetAllDates().Count();
            if (overrides > 0)
            {
                lost.Add(overrides == 1 ? "1 changed occurrence" : $"{overrides} changed occurrences");
            }
            if (deleted > 0)
            {
                lost.Add(deleted == 1 ? "1 deleted occurrence (it comes back)" : $"{deleted} deleted occurrences (they come back)");
            }
        }
        return new(moved, lost);
    }

    /// <summary>Minutes before the start of a reminder relative to the start; null for any other kind.</summary>
    private static int? BeforeStart(Alarm alarm) =>
        alarm.Trigger is { IsRelative: true, Duration: { } d } trigger && !"END".Equals(trigger.Related, StringComparison.OrdinalIgnoreCase)
            && d.ToTimeSpanUnspecified() is var span && span <= TimeSpan.Zero
            ? (int)Math.Round(-span.TotalMinutes)
            : null;

    /// <summary>A meeting or an invitation: it has attendees or an organiser. These never move to another calendar.</summary>
    public static bool IsMeeting(CalendarEvent ev) => ev.Attendees.Count > 0 || ev.Organizer is not null;

    /// <summary><see cref="IsMeeting(CalendarEvent)"/> for a stored CalDAV event.</summary>
    public static bool IsMeeting(Item item) => IsMeeting(CalDavEvents.Master(Calendar.Load(item.RawPayload)!));

    /// <summary>A TZID (IANA or Windows) as a Windows timezone, which Graph understands.</summary>
    private static TimeZoneInfo? WindowsZone(string tzId) =>
        (TimeZoneInfo.TryConvertIanaIdToWindowsId(tzId, out var windowsId) ? windowsId : tzId) is var id
        && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;

    // ---- Microsoft 365 to CalDAV ----

    private static TransferResult GraphToCalDav(Item item, JsonObject ev, Collection target, TimeZoneInfo zone, DateTimeOffset now)
    {
        var lost = new List<string>();
        var fields = GraphEventMapper.Read(item, null, zone);
        if (ev["body"] is JsonObject body && Text(body["contentType"]) == "html")
        {
            fields = fields with { Description = PlainText(Text(body["content"]) ?? "") };
            lost.Add("the description's formatting");
        }

        // Written in the event's own timezone: its wall times there, with that zone's TZID.
        var own = GraphEvents.OwnZone(ev, zone);
        if (!fields.AllDay && own.Id != zone.Id)
        {
            fields = fields with
            {
                Start = TimeZoneInfo.ConvertTime(GraphEventMapper.Local(fields.Start, zone), own).DateTime,
                End = TimeZoneInfo.ConvertTime(GraphEventMapper.Local(fields.End, zone), own).DateTime,
            };
        }
        else
        {
            own = zone;
        }
        var created = CalDavEvents.Create(target, fields, own, now);
        var calendar = Calendar.Load(created.RawPayload)!;
        var vevent = calendar.Events.Single();

        if (ev["location"]?["coordinates"] is JsonObject c && Number(c["latitude"]) is { } lat && Number(c["longitude"]) is { } lon)
        {
            vevent.GeographicLocation = new GeographicLocation(lat, lon);
        }
        if (ev["isReminderOn"] is JsonValue on && on.TryGetValue<bool>(out var reminderOn) && reminderOn)
        {
            var minutes = ev["reminderMinutesBeforeStart"] is JsonValue m && m.TryGetValue<int>(out var n) ? n : 15;
            vevent.Alarms.Add(new Alarm { Action = "DISPLAY", Description = "Reminder", Trigger = new Trigger(Duration.FromMinutes(-minutes)) });
        }
        vevent.Class = Text(ev["sensitivity"]) switch
        {
            "private" or "personal" => "PRIVATE",
            "confidential" => "CONFIDENTIAL",
            _ => null,
        };
        vevent.Priority = Text(ev["importance"]) switch
        {
            "high" => 1,
            "low" => 9,
            _ => 0,
        };
        if (ev["categories"] is JsonArray categories)
        {
            foreach (var category in categories.Select(Text).OfType<string>())
            {
                vevent.Categories.Add(category);
            }
        }
        return new(CalDavEvents.ToItem(created.Id, created.CollectionId, created.RemoteId!, null, calendar, own), lost);
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    /// <summary>An HTML body as plain text: line breaks kept, tags and the head dropped, entities decoded.</summary>
    internal static string PlainText(string html)
    {
        var text = HeadOrScript().Replace(html, "");
        text = LineBreak().Replace(text, "\n");
        text = WebUtility.HtmlDecode(Tag().Replace(text, "")).Replace((char)160, ' ');
        text = string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()));
        return BlankLines().Replace(text, "\n\n").Trim();
    }

    [GeneratedRegex(@"<(head|style|script)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadOrScript();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|li|tr|h\d)>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
