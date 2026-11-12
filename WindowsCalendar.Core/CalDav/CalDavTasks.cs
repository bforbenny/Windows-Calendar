using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Reminders;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.CalDav;

/// <summary>
/// The result of completing a Radicale task (tasks R7): <see cref="AdvancedTo"/> is the new due date when a repeating
/// task moved on and stayed open, null when the task was completed.
/// </summary>
public sealed record TaskCompletion(Item Item, DateOnly? AdvancedTo);

/// <summary>
/// Creates and edits Radicale tasks (VTODO) for the editor (tasks third slice, R7, R9). Edits change the stored iCalendar
/// object in place and touch only the fields that changed, so unknown properties and other alarms survive
/// (sync-radicale R8). The columns come from <see cref="CalDavItemMapper"/>, as for synced items. Writes need no outbox
/// handling of their own: <see cref="LocalStore"/> queues the returned item like an event.
/// </summary>
public static class CalDavTasks
{
    /// <summary>
    /// The editor's fields for a VTODO: DUE as a date, with a time when it is a DATE-TIME (in <paramref name="zone"/>);
    /// priority per tasks R4; the reminder is the first alarm's fire time; Repeat is null when it does not repeat or
    /// the Repeat panel can't show the rule.
    /// </summary>
    public static TaskFields Read(Item item, TimeZoneInfo zone)
    {
        var todo = Master(CustomTimeZones.Load(item.RawPayload)!);
        var (due, time) = ReadDue(todo, zone);
        DateTime? reminder = ReminderAlarms.ForTask(item, AccountType.CalDAV, zone) is [var first, ..]
            ? TimeZoneInfo.ConvertTime(first, zone).DateTime : null;
        return new TaskFields(todo.Summary ?? "", IsCompleted(todo), due, Importance(todo.Priority), todo.Description,
            ReadRepeat(todo, zone) is { Frequency: not RepeatFrequency.None } rule ? rule : null, reminder, time);
    }

    /// <summary>The same task in another task list, as a new item with its own id and a new href, keeping its UID (tasks R11).</summary>
    public static Item MoveTo(Item item, Collection target)
    {
        var uid = Master(CustomTimeZones.Load(item.RawPayload)!).Uid!;
        return item with { Id = Guid.NewGuid().ToString("N"), CollectionId = target.Id, RemoteId = CalDavEvents.Href(target, uid), Etag = null };
    }

    /// <summary>The task's repeat rule: <see cref="RepeatRule.None"/> when it does not repeat, null when the panel can't show it.</summary>
    public static RepeatRule? ReadRepeat(Item item, TimeZoneInfo zone) => ReadRepeat(Master(CustomTimeZones.Load(item.RawPayload)!), zone);

    /// <summary>
    /// A new task with a new UID and href (sync-radicale R7). A repeat in <paramref name="fields"/> is written as an
    /// RRULE and needs a due date.
    /// </summary>
    public static Item Create(Collection collection, TaskFields fields, TimeZoneInfo zone, DateTimeOffset now)
    {
        var uid = Guid.NewGuid().ToString();
        var calendar = new Calendar();
        var todo = new Todo { Uid = uid, Created = CalDavEvents.Utc(now), DtStamp = CalDavEvents.Utc(now), Sequence = 0, LastModified = CalDavEvents.Utc(now) };
        calendar.Todos.Add(todo);
        todo.Summary = fields.Title;
        todo.Description = CalDavEvents.Blank(fields.Notes);
        if (DueValue(calendar, fields.Due, fields.DueTime, zone) is { } due)
        {
            todo.Due = due;
        }
        todo.Priority = fields.Importance switch { "high" => 1, "low" => 9, _ => 5 };
        if (fields.Completed)
        {
            Complete(todo, now);
        }
        else
        {
            todo.Status = "NEEDS-ACTION";
        }
        if (fields.Reminder is { } reminder)
        {
            WriteReminder(todo, reminder, zone);
        }
        if (fields.Repeat is { Frequency: not RepeatFrequency.None } rule)
        {
            WriteRepeat(todo, rule, zone);
        }
        return CalDavEvents.ToItem(Guid.NewGuid().ToString("N"), collection.Id, CalDavEvents.Href(collection, uid), null, calendar, zone);
    }

    /// <summary>
    /// The item with the changed fields written into its payload; SEQUENCE, DTSTAMP and LAST-MODIFIED move on. Null when
    /// no field changed. <see cref="TaskFields.Repeat"/> is ignored (see <see cref="SetRepeat"/>). Completing a task that
    /// repeats advances it (tasks R7) and leaves it open, so a caller that must tell the two apart uses
    /// <see cref="Complete(Item, TimeZoneInfo, DateTimeOffset)"/>, or sees the result still open.
    /// </summary>
    public static Item? Edit(Item item, TaskFields fields, TimeZoneInfo zone, DateTimeOffset now) => Apply(item, fields, zone, now, out _);

    /// <summary>
    /// Ticks a task complete (tasks R7): a task with an RRULE moves its DUE (and DTSTART, by the same distance) to the
    /// next occurrence after the current due date and stays open; with no next occurrence, or no repeat, it is completed.
    /// A task that is already completed comes back unchanged.
    /// </summary>
    public static TaskCompletion Complete(Item item, TimeZoneInfo zone, DateTimeOffset now)
    {
        var fields = Read(item, zone);
        return Apply(item, fields with { Completed = true }, zone, now, out var advanced) is { } changed
            ? new TaskCompletion(changed, advanced)
            : new TaskCompletion(item, null);
    }

    /// <summary>The task reopened (STATUS=NEEDS-ACTION, COMPLETED and PERCENT-COMPLETE removed), or the item itself when it is open.</summary>
    public static Item Reopen(Item item, TimeZoneInfo zone, DateTimeOffset now) =>
        Edit(item, Read(item, zone) with { Completed = false }, zone, now) ?? item;

    /// <summary>
    /// Sets (or, for <see cref="RepeatRule.None"/>, clears) the task's RRULE; null when the rule is unchanged. The task
    /// needs a due date or start; a weekly rule on other weekdays moves the dates to the first chosen one. Throws
    /// <see cref="EditRefusedException"/> for a task without a date or with a rule or listed dates the panel can't show.
    /// </summary>
    public static Item? SetRepeat(Item item, RepeatRule rule, TimeZoneInfo zone, DateTimeOffset now)
    {
        var calendar = CustomTimeZones.Load(item.RawPayload)!;
        var todo = Master(calendar);
        var current = ReadRepeat(todo, zone) ?? throw new EditRefusedException("This repeat pattern can't be changed here.");
        if (current.Equals(rule))
        {
            return null;
        }
        WriteRepeat(todo, rule, zone);
        Stamp(todo, now);
        return CalDavEvents.ToItem(item.Id, item.CollectionId, item.RemoteId!, item.Etag, calendar, zone);
    }

    /// <summary>
    /// The item with an earlier payload put back, e.g. for undo (undo R4). The columns are derived as a task by
    /// <see cref="CalDavItemMapper"/>; queue the result with <see cref="LocalStore.UpdateItem"/>, which merges it with
    /// an unsent change.
    /// </summary>
    public static Item Restore(Item item, string payload, TimeZoneInfo zone)
    {
        var restored = CalDavEvents.Restore(item, payload, zone);
        return restored.Kind == ItemKind.Task ? restored : throw new ArgumentException("The payload is not a task.", nameof(payload));
    }

    private static Item? Apply(Item item, TaskFields fields, TimeZoneInfo zone, DateTimeOffset now, out DateOnly? advancedTo)
    {
        advancedTo = null;
        var calendar = CustomTimeZones.Load(item.RawPayload)!;
        var todo = Master(calendar);
        var old = Read(item, zone);
        var changed = false;

        if (old.Title != fields.Title)
        {
            todo.Summary = fields.Title;
            changed = true;
        }
        if (Normalise(old.Notes) != Normalise(fields.Notes))
        {
            todo.Description = CalDavEvents.Blank(fields.Notes);
            changed = true;
        }
        var newTime = fields.Due is null ? null : fields.DueTime;
        if (old.Due != fields.Due || old.DueTime != newTime)
        {
            MoveDue(calendar, todo, old, fields.Due, newTime, zone);
            changed = true;
        }
        if (old.Importance != fields.Importance)
        {
            todo.Priority = fields.Importance switch
            {
                "high" => todo.Priority is >= 1 and <= 4 ? todo.Priority : 1,
                "low" => todo.Priority is >= 6 and <= 9 ? todo.Priority : 9,
                _ => todo.Priority is 0 or 5 ? todo.Priority : 5,
            };
            changed = true;
        }
        if (fields.Reminder != old.Reminder)
        {
            WriteReminder(todo, fields.Reminder, zone);
            changed = true;
        }
        if (fields.Completed != old.Completed)
        {
            if (!fields.Completed)
            {
                Reopen(todo);
            }
            else if (Advance(todo, zone) is { } next)
            {
                advancedTo = next;
            }
            else
            {
                Complete(todo, now);
            }
            changed = true;
        }
        if (!changed)
        {
            return null;
        }
        Stamp(todo, now);
        return CalDavEvents.ToItem(item.Id, item.CollectionId, item.RemoteId!, item.Etag, calendar, zone);
    }

    // ---- Fields ----

    internal static Todo Master(Calendar calendar) =>
        calendar.Todos.FirstOrDefault(t => t.RecurrenceIdentifier is null) ?? calendar.Todos.First();

    private static bool IsCompleted(Todo todo) =>
        todo.Completed is not null || string.Equals(todo.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase);

    /// <summary>R4: 1-4 high, 6-9 low, 5 and 0 (and none) normal.</summary>
    private static string Importance(int priority) => priority switch { >= 1 and <= 4 => "high", >= 6 and <= 9 => "low", _ => "normal" };

    private static string Normalise(string? text) => (text ?? "").Replace("\r\n", "\n").Trim();

    private static (DateOnly? Due, TimeOnly? Time) ReadDue(Todo todo, TimeZoneInfo zone)
    {
        if (todo.Due is not { } due)
        {
            return (null, null);
        }
        if (!due.HasTime)
        {
            return (DateOnly.FromDateTime(due.Value), null);
        }
        var wall = CalDavEvents.Wall(due, zone);
        return (DateOnly.FromDateTime(wall), TimeOnly.FromDateTime(wall));
    }

    /// <summary>A DATE, or a DATE-TIME in the Windows timezone (its VTIMEZONE added) when there is a time.</summary>
    private static CalDateTime? DueValue(Calendar calendar, DateOnly? due, TimeOnly? time, TimeZoneInfo zone) =>
        due is not { } day ? null
        : time is not { } t ? new CalDateTime(day)
        : CalDavEvents.InZone(calendar, CalDavEvents.ToUtc(day.ToDateTime(t), zone), zone);

    /// <summary>Writes the new DUE; DTSTART, when there is one, moves with it so the distance between them stays.</summary>
    private static void MoveDue(Calendar calendar, Todo todo, TaskFields old, DateOnly? due, TimeOnly? time, TimeZoneInfo zone)
    {
        // DTSTART first: setting it recalculates DUE in Ical.Net.
        if (todo.DtStart is { } start && due is { } day && old.Due is { } was)
        {
            todo.DtStart = CalDavRecurrence.Shift(start, day.ToDateTime(time ?? TimeOnly.MinValue) - was.ToDateTime(old.DueTime ?? TimeOnly.MinValue));
        }
        if (DueValue(calendar, due, time, zone) is { } value)
        {
            todo.Due = value;
        }
        else
        {
            // A relative alarm needs a DUE or DTSTART to count from: keep each reminder as the time it was.
            MakeAlarmsAbsolute(todo, zone);
            todo.Properties.Remove("DUE");
        }
    }

    // Ical.Net writes a trigger's RELATED and a fixed time only through these parameters.
    private static Trigger FromEnd(Duration offset)
    {
        var trigger = new Trigger(offset);
        trigger.Parameters.Set("RELATED", "END");
        return trigger;
    }

    private static Trigger Absolute(DateTime utc)
    {
        var trigger = new Trigger { DateTime = new CalDateTime(utc, "UTC") };
        trigger.Parameters.Set("VALUE", "DATE-TIME");
        return trigger;
    }

    private static void MakeAlarmsAbsolute(Todo todo, TimeZoneInfo zone)
    {
        DateTimeOffset? startAt = todo.DtStart is { } s ? OccurrenceExpander.ToUtc(s, zone) : null;
        DateTimeOffset? dueAt = todo.Due is { } d ? OccurrenceExpander.ToUtc(d, zone) : null;
        foreach (var alarm in todo.Alarms)
        {
            if (alarm.Trigger is { IsRelative: true, Duration: { } offset } trigger
                && (string.Equals(trigger.Related, "END", StringComparison.OrdinalIgnoreCase) ? dueAt ?? startAt : startAt ?? dueAt) is { } anchor)
            {
                alarm.Trigger = Absolute((anchor + offset.ToTimeSpanUnspecified()).UtcDateTime);
            }
        }
    }

    private static void Complete(Todo todo, DateTimeOffset now)
    {
        todo.Status = "COMPLETED";
        todo.Completed = CalDavEvents.Utc(now);
        todo.PercentComplete = 100;
    }

    private static void Reopen(Todo todo)
    {
        todo.Properties.Remove("COMPLETED");
        todo.Properties.Remove("PERCENT-COMPLETE");
        todo.Status = "NEEDS-ACTION";
    }

    private static void Stamp(Todo todo, DateTimeOffset now)
    {
        todo.Sequence += 1;
        todo.DtStamp = CalDavEvents.Utc(now);
        todo.LastModified = CalDavEvents.Utc(now);
    }

    // ---- Reminder: the first DISPLAY alarm; other alarms are kept ----

    private static Alarm? FirstDisplay(Todo todo) =>
        todo.Alarms.FirstOrDefault(a => string.Equals(a.Action, "DISPLAY", StringComparison.OrdinalIgnoreCase) && a.Trigger is not null);

    /// <summary>
    /// Sets the first DISPLAY alarm, adds one when there is none, or removes it for null. With a due date the trigger is
    /// the offset from it (from DUE, as RELATED=END), so it moves with the due date and repeats;
    /// without one it is the fixed time.
    /// </summary>
    private static void WriteReminder(Todo todo, DateTime? reminder, TimeZoneInfo zone)
    {
        var first = FirstDisplay(todo);
        if (reminder is not { } wall)
        {
            if (first is not null)
            {
                todo.Children.Remove(first);
            }
            return;
        }
        var at = CalDavEvents.ToUtc(wall, zone);
        var trigger = todo.Due is { } due
            ? FromEnd(Duration.FromMinutes((int)Math.Round((at - OccurrenceExpander.ToUtc(due, zone).UtcDateTime).TotalMinutes)))
            : Absolute(at);
        if (first is null)
        {
            todo.Alarms.Add(new Alarm { Action = "DISPLAY", Description = "Reminder", Trigger = trigger });
        }
        else
        {
            first.Trigger = trigger;
        }
    }

    // ---- Repeat (tasks R7, R7b) ----

    private static RepeatRule? ReadRepeat(Todo todo, TimeZoneInfo zone)
    {
        if (todo.RecurrenceRule is null && !todo.RecurrenceDates.GetAllDates().Any())
        {
            return RepeatRule.None;
        }
        return (todo.DtStart ?? todo.Due) is { } anchor ? CalDavRecurrence.ReadRule(todo, anchor, zone) : null;
    }

    private static void WriteRepeat(Todo todo, RepeatRule rule, TimeZoneInfo zone)
    {
        if (todo.RecurrenceDates.GetAllDates().Any())
        {
            throw new EditRefusedException("This task repeats on listed dates, so its repeat pattern can't be changed here.");
        }
        if (rule.Frequency == RepeatFrequency.None)
        {
            todo.Properties.Remove("RRULE");
            todo.Properties.Remove("EXDATE");
            return;
        }
        var anchor = todo.DtStart ?? todo.Due ?? throw new EditRefusedException("A repeating task needs a due date.");
        var start = DateOnly.FromDateTime(anchor.Value);
        if (rule.Frequency == RepeatFrequency.Weekly && rule.Weekdays.Count > 0 && !rule.Weekdays.Contains(start.DayOfWeek))
        {
            // The dates move to the first chosen weekday after them, so the first one is an occurrence.
            var days = TimeSpan.FromDays(Enumerable.Range(1, 6).First(n => rule.Weekdays.Contains(start.AddDays(n).DayOfWeek)));
            if (todo.DtStart is { } s)
            {
                todo.DtStart = CalDavRecurrence.Shift(s, days);
            }
            if (todo.Due is { } d)
            {
                todo.Due = CalDavRecurrence.Shift(d, days);
            }
            anchor = todo.DtStart ?? todo.Due!;
            start = DateOnly.FromDateTime(anchor.Value);
        }
        todo.Properties.Remove("EXDATE");
        var old = todo.RecurrenceRule;
        todo.Properties.Remove("RRULE");
        todo.RecurrenceRule = CalDavRecurrence.ToRecurrenceRule(rule, anchor, start, old, zone);
    }

    /// <summary>
    /// Moves a repeating task to its next occurrence (tasks R7) and returns the new due date; null, with the task
    /// untouched, when it has no rule, no due date or no occurrence after the due date. The occurrences are those of
    /// DTSTART (DUE when there is none), the due date following each by the task's own distance. A COUNT is reduced by
    /// the occurrences passed, as the dates themselves move on.
    /// </summary>
    private static DateOnly? Advance(Todo todo, TimeZoneInfo zone)
    {
        if (todo.RecurrenceRule is not { } rule || todo.Due is not { } due)
        {
            return null;
        }
        var start = todo.DtStart ?? due;
        var distance = due.HasTime && start.HasTime ? due.Value - start.Value : TimeSpan.FromDays(Math.Round((due.Value.Date - start.Value.Date).TotalDays));
        var dueAt = OccurrenceExpander.ToUtc(due, zone);

        var work = todo.Copy<Todo>()!;
        if (work.DtStart is null)
        {
            work.DtStart = due.HasTime ? new CalDateTime(due.Value, due.TzId) : new CalDateTime(DateOnly.FromDateTime(due.Value));
        }
        // An occurrence's due date is its start plus the distance, so only starts after DUE minus the distance can qualify.
        foreach (var occurrence in work.GetOccurrences(OccurrenceExpander.ToCal((dueAt - distance).AddDays(-1))).Take(5000))
        {
            var next = CalDavRecurrence.Shift(occurrence.Period.StartTime, distance);
            if (OccurrenceExpander.ToUtc(next, zone) > dueAt)
            {
                if (todo.DtStart is not null)
                {
                    todo.DtStart = occurrence.Period.StartTime;
                }
                todo.Due = next;
                if (rule.Count is { } count)
                {
                    rule.Count = count - RawOccurrencesBefore(work, occurrence.Period.StartTime, zone);
                }
                Reopen(todo);
                return due.HasTime ? DateOnly.FromDateTime(CalDavEvents.Wall(next, zone)) : DateOnly.FromDateTime(next.Value);
            }
        }
        return null;
    }

    /// <summary>The rule's own occurrences before <paramref name="until"/>; COUNT includes the dates EXDATE excludes.</summary>
    private static int RawOccurrencesBefore(Todo work, CalDateTime until, TimeZoneInfo zone)
    {
        var raw = work.Copy<Todo>()!;
        raw.Properties.Remove("EXDATE");
        var limit = OccurrenceExpander.ToUtc(until, zone);
        return raw.GetOccurrences(OccurrenceExpander.ToCal(OccurrenceExpander.ToUtc(raw.DtStart!, zone).AddDays(-1)))
            .TakeWhile(o => OccurrenceExpander.ToUtc(o.Period.StartTime, zone) < limit).Count();
    }
}
