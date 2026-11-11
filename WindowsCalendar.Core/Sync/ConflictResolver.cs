using System.Globalization;
using System.Text.Json.Nodes;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Sync;

/// <summary>One field that differs between the local version and the server version, as display text.</summary>
public sealed record FieldDifference(string Field, string Yours, string Server);

/// <summary>
/// Resolving a kept conflict (conflict-resolution R6, second slice): what differs between the saved local version and
/// the server version now on the calendar, and the item that sends the local version again over the current server one.
/// </summary>
public static class ConflictResolver
{
    /// <summary>
    /// The differences in title, start, end, all-day, location, show-as and reminder, in that order. A Microsoft 365
    /// event compares only the fields its local edit changed; a CalDAV event compares them all. Times are in
    /// <paramref name="zone"/>: "17:30" on the day the server version starts, else "Wed 12 Nov 17:30"; all-day times are
    /// dates (End is the exclusive date). A local delete (null payload) has none; a Microsoft 365 task compares its title.
    /// </summary>
    public static IReadOnlyList<FieldDifference> Differences(string? localPayload, Item current, bool graph, TimeZoneInfo zone)
    {
        if (localPayload is null)
        {
            return [];
        }
        if (current.Kind == ItemKind.Task)
        {
            var yours = LocalStore.PayloadTitle(localPayload) ?? "";
            var server = current.Summary ?? "";
            return yours == server ? [] : [new FieldDifference("Title", yours, server)];
        }
        if (graph)
        {
            var local = GraphEvents.Parse(localPayload);
            var changed = GraphEvents.Changed(local);
            var localFields = GraphEventMapper.Read(GraphEventMapper.Rebuild(current, localPayload, zone), null, zone);
            var serverFields = GraphEventMapper.Read(current, null, zone);
            return Compare(localFields, serverFields, GraphReminder(local), GraphReminder(GraphEvents.Parse(current.RawPayload)),
                title: changed.Contains("subject"), start: changed.Contains("start"), end: changed.Contains("end"),
                allDay: changed.Contains("isAllDay"), location: changed.Contains("location"), showAs: changed.Contains("showAs"),
                reminder: changed.Contains("isReminderOn") || changed.Contains("reminderMinutesBeforeStart"));
        }
        if (CalDavItemMapper.Map(current.RemoteId ?? "", current.Etag ?? "", localPayload, zone) is not { } mapped)
        {
            return [];
        }
        return Compare(CalDavEvents.Read(mapped, zone), CalDavEvents.Read(current, zone), CalDavReminder(localPayload),
            CalDavReminder(current.RawPayload), true, true, true, true, true, true, true);
    }

    /// <summary>
    /// The item that sends the local version over the server one (the caller passes it to <c>LocalStore.UpdateItem</c>).
    /// Microsoft 365: the current server JSON with the fields the local edit changed copied over and listed as changed,
    /// so a PATCH sends only those. CalDAV: the local iCalendar text under the current id, collection, href and etag.
    /// </summary>
    public static Item UseMine(Item current, string localPayload, bool graph, TimeZoneInfo zone)
    {
        if (graph)
        {
            var ev = GraphEvents.Parse(current.RawPayload);
            var local = GraphEvents.Parse(localPayload);
            var changed = GraphEvents.Changed(local);
            foreach (var name in changed)
            {
                ev[name] = local[name]?.DeepClone();
            }
            ev[GraphEvents.ChangedKey] = new JsonArray(changed.Order().Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
            return current.Kind == ItemKind.Task
                ? GraphTaskMapper.Rebuild(current, ev.ToJsonString(), zone)
                : GraphEventMapper.Rebuild(current, ev.ToJsonString(), zone);
        }
        var mapped = CalDavItemMapper.Map(current.RemoteId ?? "", current.Etag ?? "", localPayload, zone)
            ?? throw new InvalidOperationException("The saved local version cannot be read.");
        return mapped with { Id = current.Id, CollectionId = current.CollectionId, RemoteId = current.RemoteId, Etag = current.Etag };
    }

    private static List<FieldDifference> Compare(EventFields mine, EventFields theirs, string myReminder, string theirReminder,
        bool title, bool start, bool end, bool allDay, bool location, bool showAs, bool reminder)
    {
        var day = theirs.Start.Date;
        var list = new List<FieldDifference>();
        void Add(string field, string yours, string server)
        {
            if (yours != server)
            {
                list.Add(new FieldDifference(field, yours, server));
            }
        }
        if (title)
        {
            Add("Title", Text(mine.Title), Text(theirs.Title));
        }
        if (start)
        {
            Add("Start", Time(mine.Start, mine.AllDay, day), Time(theirs.Start, theirs.AllDay, day));
        }
        if (end)
        {
            Add("End", Time(mine.End, mine.AllDay, day), Time(theirs.End, theirs.AllDay, day));
        }
        if (allDay)
        {
            Add("All day", mine.AllDay ? "Yes" : "No", theirs.AllDay ? "Yes" : "No");
        }
        if (location)
        {
            Add("Location", Text(mine.Location), Text(theirs.Location));
        }
        if (showAs)
        {
            Add("Show as", ShowAsText(mine.ShowAs), ShowAsText(theirs.ShowAs));
        }
        if (reminder)
        {
            Add("Reminder", myReminder, theirReminder);
        }
        return list;
    }

    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "(none)" : value.Trim();

    private static string Time(DateTime wall, bool allDay, DateTime day) =>
        allDay ? wall.ToString("ddd d MMM", CultureInfo.CurrentCulture)
        : wall.Date == day ? wall.ToString("HH:mm", CultureInfo.CurrentCulture)
        : wall.ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture);

    private static string ShowAsText(ShowAs value) => value switch
    {
        ShowAs.OutOfOffice => "Out of office",
        ShowAs.WorkingElsewhere => "Working elsewhere",
        _ => value.ToString(),
    };

    private static string Reminder(int? minutes) => minutes is { } m ? $"{m} min before" : "None";

    private static string GraphReminder(JsonObject ev) =>
        Reminder(ev["isReminderOn"] is JsonValue on && on.TryGetValue<bool>(out var isOn) && isOn
            && ev["reminderMinutesBeforeStart"] is JsonValue m && m.TryGetValue<int>(out var minutes) ? minutes : null);

    /// <summary>The first reminder before the start, as the app writes it; any other kind counts as none.</summary>
    private static string CalDavReminder(string payload)
    {
        try
        {
            var ev = CalDavEvents.Master(Ical.Net.Calendar.Load(payload)!);
            foreach (var alarm in ev.Alarms)
            {
                if (alarm.Trigger is { IsRelative: true, Duration: { } d } trigger
                    && !"END".Equals(trigger.Related, StringComparison.OrdinalIgnoreCase)
                    && d.ToTimeSpanUnspecified() is var span && span <= TimeSpan.Zero)
                {
                    return Reminder((int)Math.Round(-span.TotalMinutes));
                }
            }
        }
        catch (Exception)
        {
            // An unreadable payload has no reminder to show.
        }
        return Reminder(null);
    }
}
