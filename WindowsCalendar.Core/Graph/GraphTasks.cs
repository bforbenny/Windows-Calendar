using System.Text.Json.Nodes;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Graph;

/// <summary>
/// Local edits of To Do tasks (sync-ms365 third slice, R12). As with events, an edit changes the stored JSON and lists
/// the fields it changed under <see cref="GraphEvents.ChangedKey"/>, and the PATCH sends only those
/// (<see cref="GraphEvents.PatchBody"/>). Due dates are written as midnight in the Windows timezone.
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
        if (fields.Due is { } due)
        {
            task["dueDateTime"] = Due(due, zone);
        }
        return GraphTaskMapper.Rebuild(new Item(Guid.NewGuid().ToString("N"), list.Id, ItemKind.Task, null, "{}"), task.ToJsonString(), zone);
    }

    /// <summary>The task with <paramref name="fields"/>, or null when nothing changed.</summary>
    public static Item? Edit(Item item, TaskFields fields, TimeZoneInfo zone)
    {
        var old = GraphTaskMapper.Read(item);
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
        if (fields.Due != old.Due)
        {
            task["dueDateTime"] = fields.Due is { } due ? Due(due, zone) : null;
            changed.Add("dueDateTime");
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
        if (!any)
        {
            return null;
        }
        task[GraphEvents.ChangedKey] = new JsonArray(changed.Order().Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
        return GraphTaskMapper.Rebuild(item, task.ToJsonString(), zone);
    }

    /// <summary>
    /// Whether the app may change the task: not in a read-only list (Flagged emails, sync-ms365 third slice).
    /// A recurring task may only be completed or reopened (recurring-events R8).
    /// </summary>
    public static bool CanEdit(Collection list) => !list.ReadOnly;

    private static JsonObject Body(string? notes) => new() { ["contentType"] = "text", ["content"] = notes ?? "" };

    private static JsonObject Due(DateOnly due, TimeZoneInfo zone) =>
        GraphEvents.DateTimeTimeZone(due.ToDateTime(TimeOnly.MinValue), zone);

    private static string Normalise(string? text) => (text ?? "").Replace("\r\n", "\n").Trim();
}
