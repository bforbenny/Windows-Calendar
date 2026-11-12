using System.Text.Json.Nodes;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Graph;

/// <summary>
/// Local edits of To Do tasks (sync-ms365 third slice, R12). As with events, an edit changes the stored JSON and lists
/// the fields it changed under <see cref="GraphEvents.ChangedKey"/>, and the PATCH sends only those
/// (<see cref="GraphEvents.PatchBody"/>). Due dates are written as midnight in the Windows timezone. A repeat is the
/// task's <c>recurrence</c> (tasks R7b); it needs a due date, which it sets when there is none.
/// </summary>
public static class GraphTasks
{
    /// <summary>A new task in <paramref name="list"/>.</summary>
    public static Item Create(Collection list, TaskFields fields, TimeZoneInfo zone)
    {
        var task = new JsonObject
        {
            ["title"] = fields.Title,
            ["importance"] = fields.Importance,
            ["status"] = fields.Completed ? "completed" : "notStarted",
        };
        if (!string.IsNullOrEmpty(fields.Notes))
        {
            task["body"] = Body(fields.Notes);
        }
        var rule = Repeating(fields.Repeat);
        var due = rule is null ? fields.Due : RepeatStart(rule, fields.Due, zone);
        if (due is { } date)
        {
            task["dueDateTime"] = Due(date, zone);
        }
        if (rule is not null)
        {
            task["recurrence"] = GraphRecurrence.Recurrence(rule, due!.Value, zone);
        }
        if (fields.Reminder is { } reminder)
        {
            SetReminder(task, reminder, zone);
        }
        return GraphTaskMapper.Rebuild(new Item(Guid.NewGuid().ToString("N"), list.Id, ItemKind.Task, null, "{}"), task.ToJsonString(), zone);
    }

    /// <summary>Fields a create may copy from a stored task (a move to another list).</summary>
    private static readonly string[] Writable =
    [
        "title", "body", "importance", "status", "dueDateTime", "recurrence", "isReminderOn", "reminderDateTime", "categories",
        "startDateTime", "completedDateTime",
    ];

    /// <summary>A copy for another To Do list (a create there; the caller deletes the original), with a new id (tasks R11).</summary>
    public static Item MoveTo(Item item, Collection target, TimeZoneInfo zone)
    {
        var task = GraphEvents.Parse(item.RawPayload);
        var copy = new JsonObject();
        foreach (var name in Writable)
        {
            if (task[name] is { } value)
            {
                copy[name] = value.DeepClone();
            }
        }
        return GraphTaskMapper.Rebuild(new Item(Guid.NewGuid().ToString("N"), target.Id, ItemKind.Task, null, "{}"), copy.ToJsonString(), zone);
    }

    /// <summary>The task with <paramref name="fields"/>, or null when nothing changed.</summary>
    public static Item? Edit(Item item, TaskFields fields, TimeZoneInfo zone)
    {
        var old = GraphTaskMapper.Read(item, zone);
        var task = GraphEvents.Parse(item.RawPayload);
        var changed = GraphEvents.Changed(task);
        var any = false;
        if (fields.Title != old.Title)
        {
            task["title"] = fields.Title;
            changed.Add("title");
            any = true;
        }
        if (Normalise(fields.Notes) != Normalise(old.Notes))
        {
            task["body"] = Body(fields.Notes);
            changed.Add("body");
            any = true;
        }
        // A repeat that changed, or a moved due date under one, rewrites the pattern from the due date (tasks R7b).
        var rule = Repeating(fields.Repeat);
        var oldRule = Repeating(old.Repeat);
        var restart = rule is not null && (!rule.Equals(oldRule) || fields.Due != old.Due);
        var due = restart ? RepeatStart(rule!, fields.Due, zone) : fields.Due;
        if (due != old.Due)
        {
            task["dueDateTime"] = due is { } date ? Due(date, zone) : null;
            changed.Add("dueDateTime");
            any = true;
        }
        if (restart)
        {
            task["recurrence"] = GraphRecurrence.Recurrence(rule!, due!.Value, zone);
            changed.Add("recurrence");
            any = true;
        }
        else if (rule is null && oldRule is not null)
        {
            task["recurrence"] = null;
            changed.Add("recurrence");
            any = true;
        }
        if (fields.Importance != old.Importance)
        {
            task["importance"] = fields.Importance;
            changed.Add("importance");
            any = true;
        }
        if (fields.Completed != old.Completed)
        {
            task["status"] = fields.Completed ? "completed" : "notStarted";
            changed.Add("status");
            any = true;
        }
        if (fields.Reminder != old.Reminder)
        {
            SetReminder(task, fields.Reminder, zone);
            changed.UnionWith(fields.Reminder is null ? ["isReminderOn"] : ["isReminderOn", "reminderDateTime"]);
            any = true;
        }
        if (!any)
        {
            return null;
        }
        task[GraphEvents.ChangedKey] = new JsonArray(changed.Order().Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
        return GraphTaskMapper.Rebuild(item, task.ToJsonString(), zone);
    }

    /// <summary>
    /// Undo (undo R4): the fields the change wrote are set back from <paramref name="payloadBefore"/> and listed as
    /// changed, together with any unsent ones, so the next PATCH sends them again (coalescing with an unsent change).
    /// </summary>
    public static Item Restore(Item item, string payloadBefore, TimeZoneInfo zone)
    {
        var task = GraphEvents.Parse(item.RawPayload);
        var before = GraphEvents.Parse(payloadBefore);
        var changed = GraphEvents.Changed(task);
        changed.UnionWith(GraphEvents.Changed(before));
        foreach (var name in changed)
        {
            task[name] = before[name]?.DeepClone();
        }
        task[GraphEvents.ChangedKey] = new JsonArray(changed.Order().Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
        return GraphTaskMapper.Rebuild(item, task.ToJsonString(), zone);
    }

    /// <summary>
    /// Whether the app may change the task: not in a read-only list (Flagged emails, sync-ms365 third slice).
    /// A recurring task whose pattern the repeat panel can't show may only be completed or reopened (recurring-events R8).
    /// </summary>
    public static bool CanEdit(Collection list) => !list.ReadOnly;

    /// <summary>The rule when it repeats, else null.</summary>
    private static RepeatRule? Repeating(RepeatRule? rule) => rule is { Frequency: not RepeatFrequency.None } ? rule : null;

    /// <summary>
    /// The first due date of a repeating task: the due date, or today when there is none, moved forward to the first
    /// chosen weekday of a weekly rule (as for events).
    /// </summary>
    private static DateOnly RepeatStart(RepeatRule rule, DateOnly? due, TimeZoneInfo zone)
    {
        var day = due ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, zone));
        return GraphRecurrence.Dates(rule.Frequency == RepeatFrequency.Weekly && rule.Weekdays.Count == 0
            ? rule with { Weekdays = [day.DayOfWeek] } : rule, day).First();
    }

    /// <summary>isReminderOn, and the time in <paramref name="zone"/> when on (reminders-notifications R5a).</summary>
    private static void SetReminder(JsonObject task, DateTime? reminder, TimeZoneInfo zone)
    {
        task["isReminderOn"] = reminder is not null;
        if (reminder is { } at)
        {
            task["reminderDateTime"] = GraphEvents.DateTimeTimeZone(at, zone);
        }
    }

    private static JsonObject Body(string? notes) => new() { ["contentType"] = "text", ["content"] = notes ?? "" };

    private static JsonObject Due(DateOnly due, TimeZoneInfo zone) =>
        GraphEvents.DateTimeTimeZone(due.ToDateTime(TimeOnly.MinValue), zone);

    private static string Normalise(string? text) => (text ?? "").Replace("\r\n", "\n").Trim();
}
