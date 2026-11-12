using System.Globalization;
using System.Text.Json.Nodes;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;

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
    /// dates (End is the exclusive date). A local delete (null payload) has none; a Microsoft 365 task compares title, completed, due date,
    /// importance, notes, repeat and reminder, again only the fields the local edit changed; a Radicale task compares all of them and its due time.
    /// </summary>
    public static IReadOnlyList<FieldDifference> Differences(string? localPayload, Item current, bool graph, TimeZoneInfo zone)
    {
        if (localPayload is null)
        {
            return [];
        }
        if (current.Kind == ItemKind.Task)
        {
            return TaskDifferences(localPayload, current, graph, zone);
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

    /// <summary>
    /// The occurrences of a CalDAV repeating series that differ between the saved local payload and the server one
    /// (conflict-resolution R6): overrides (RECURRENCE-ID events) present on one side only or with different fields, and
    /// EXDATEs on one side only. There is no stored base, so each line says what each side has: "Mon 3 Nov 09:00: yours
    /// changed · server unchanged". Times are in <paramref name="zone"/>, oldest first; empty when they match.
    /// </summary>
    public static IReadOnlyList<string> SeriesOccurrenceDifferences(string localPayload, string serverPayload, TimeZoneInfo zone)
    {
        var mine = Occurrences(localPayload, zone);
        var theirs = Occurrences(serverPayload, zone);
        var lines = new List<(DateTimeOffset At, bool AllDay, string Text)>();
        foreach (var at in mine.Overrides.Keys.Union(theirs.Overrides.Keys))
        {
            var hasMine = mine.Overrides.TryGetValue(at, out var m);
            var hasTheirs = theirs.Overrides.TryGetValue(at, out var t);
            if (hasMine && hasTheirs && m.Fields == t.Fields)
            {
                continue;
            }
            lines.Add((at, hasMine ? m.AllDay : t.AllDay,
                $"yours {(hasMine ? "changed" : "unchanged")} · server {(hasTheirs ? "changed" : "unchanged")}"));
        }
        foreach (var at in mine.Excluded.Except(theirs.Excluded))
        {
            lines.Add((at, mine.AllDayExcluded.Contains(at), "yours deleted · server unchanged"));
        }
        foreach (var at in theirs.Excluded.Except(mine.Excluded))
        {
            lines.Add((at, theirs.AllDayExcluded.Contains(at), "yours unchanged · server deleted"));
        }
        var culture = CultureInfo.CurrentCulture;
        return lines.OrderBy(l => l.At).Select(l =>
        {
            var local = TimeZoneInfo.ConvertTime(l.At, zone);
            return $"{local.ToString(l.AllDay ? "ddd d MMM" : "ddd d MMM HH:mm", culture)}: {l.Text}";
        }).ToList();
    }

    private sealed record SeriesOccurrences(Dictionary<DateTimeOffset, (EventFields Fields, bool AllDay)> Overrides,
        HashSet<DateTimeOffset> Excluded, HashSet<DateTimeOffset> AllDayExcluded);

    private static SeriesOccurrences Occurrences(string payload, TimeZoneInfo zone)
    {
        var calendar = CustomTimeZones.Load(payload) ?? throw new InvalidOperationException("The payload cannot be read.");
        var master = CalDavEvents.Master(calendar);
        var overrides = new Dictionary<DateTimeOffset, (EventFields, bool)>();
        foreach (var ev in calendar.Events.Where(e => e.RecurrenceIdentifier is not null))
        {
            var id = ev.RecurrenceIdentifier!.StartTime;
            overrides[OccurrenceExpander.ToUtc(id, zone)] = (CalDavEvents.Read(ev, zone).Walls(zone), !id.HasTime);
        }
        var excluded = new HashSet<DateTimeOffset>();
        var allDay = new HashSet<DateTimeOffset>();
        foreach (var date in master.ExceptionDates.GetAllDates())
        {
            var at = OccurrenceExpander.ToUtc(date, zone);
            excluded.Add(at);
            if (!date.HasTime)
            {
                allDay.Add(at);
            }
        }
        return new SeriesOccurrences(overrides, excluded, allDay);
    }

    /// <summary>
    /// The saved local version as an item of its own, for restoring it when the item is gone (conflict-resolution R3):
    /// the payload read as a stored item of <paramref name="graph"/>'s kind, under the conflict's item and collection ids.
    /// </summary>
    public static Item RestoreSource(string localPayload, string itemId, string collectionId, ItemKind kind, bool graph, TimeZoneInfo zone)
    {
        if (!graph)
        {
            var mapped = CalDavItemMapper.Map("", "", localPayload, zone)
                ?? throw new InvalidOperationException("The saved local version cannot be read.");
            return mapped with { Id = itemId, CollectionId = collectionId };
        }
        var item = new Item(itemId, collectionId, kind, null, localPayload);
        return kind == ItemKind.Task ? GraphTaskMapper.Rebuild(item, localPayload, zone) : GraphEventMapper.Rebuild(item, localPayload, zone);
    }

    private static List<FieldDifference> TaskDifferences(string localPayload, Item current, bool graph, TimeZoneInfo zone)
    {
        HashSet<string>? changed = null;
        Graph.TaskFields mine, theirs;
        if (graph)
        {
            changed = GraphEvents.Changed(GraphEvents.Parse(localPayload));
            mine = GraphTaskMapper.Read(GraphTaskMapper.Rebuild(current, localPayload, zone), zone);
            theirs = GraphTaskMapper.Read(current, zone);
        }
        else
        {
            if (CalDavItemMapper.Map(current.RemoteId ?? "", current.Etag ?? "", localPayload, zone) is not { Kind: ItemKind.Task } local)
            {
                return [];
            }
            mine = CalDavTasks.Read(local, zone);
            theirs = CalDavTasks.Read(current, zone);
        }
        var list = new List<FieldDifference>();
        void Add(string[] names, string field, string yours, string server)
        {
            if ((changed is null || names.Any(changed.Contains)) && yours != server)
            {
                list.Add(new FieldDifference(field, yours, server));
            }
        }
        Add(["title"], "Title", Text(mine.Title), Text(theirs.Title));
        Add(["status"], "Completed", mine.Completed ? "Yes" : "No", theirs.Completed ? "Yes" : "No");
        Add(["dueDateTime"], "Due", TaskDate(mine.Due, mine.DueTime), TaskDate(theirs.Due, theirs.DueTime));
        Add(["importance"], "Importance", mine.Importance, theirs.Importance);
        Add(["body"], "Notes", Text(mine.Notes), Text(theirs.Notes));
        Add(["recurrence"], "Repeat", RepeatText(mine.Repeat), RepeatText(theirs.Repeat));
        Add(["isReminderOn", "reminderDateTime"], "Reminder", TaskReminder(mine.Reminder), TaskReminder(theirs.Reminder));
        return list;
    }

    private static string TaskDate(DateOnly? day, TimeOnly? time) =>
        day is { } d ? d.ToString("d", CultureInfo.CurrentCulture) + (time is { } t ? " " + t.ToString("t", CultureInfo.CurrentCulture) : "") : "(none)";

    private static string TaskReminder(DateTime? at) =>
        at is { } t ? t.ToString("g", CultureInfo.CurrentCulture) : "None";

    private static string RepeatText(RepeatRule? rule)
    {
        if (rule is null or { Frequency: RepeatFrequency.None })
        {
            return "Doesn't repeat";
        }
        var unit = rule.Frequency switch
        {
            RepeatFrequency.Daily => "day",
            RepeatFrequency.Weekly => "week",
            RepeatFrequency.Monthly => "month",
            _ => "year",
        };
        var text = rule.Interval > 1 ? $"Every {rule.Interval} {unit}s" : $"Every {unit}";
        if (rule.Weekdays.Count > 0)
        {
            var names = CultureInfo.CurrentCulture.DateTimeFormat;
            text += " on " + string.Join(", ", rule.Weekdays.Select(d => names.GetAbbreviatedDayName(d)));
        }
        return text;
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
        allDay ? wall.ToString(DateFormats.WeekdayMonthDay(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture)
        : wall.Date == day ? wall.ToString("t", CultureInfo.CurrentCulture)
        : wall.ToString(DateFormats.WeekdayMonthDay(CultureInfo.CurrentCulture) + " " + CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern,
            CultureInfo.CurrentCulture);

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
