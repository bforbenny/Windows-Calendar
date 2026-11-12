using System.Text.Json.Nodes;
using Ical.Net.CalendarComponents;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Sync;

/// <summary>
/// Moving a task to another task list (tasks R11), the task counterpart of <see cref="EventTransfer"/>. Within a backend
/// the stored task is copied (a To Do task gets a new id; a Radicale task keeps its UID and gets a new href). Between
/// To Do and Radicale the fields are mapped: title, notes, due date, completed, importance, first reminder and a repeat
/// rule the Repeat panel can express. The result lists what cannot be taken along, shown before the move.
/// </summary>
public static class TaskTransfer
{
    /// <summary>
    /// The task as it will be in <paramref name="target"/>. Throws <see cref="EditRefusedException"/> for a repeat rule
    /// the Repeat panel can't express.
    /// </summary>
    public static TransferResult Move(Item item, bool fromGraph, Collection target, bool toGraph, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (fromGraph)
        {
            var lost = GraphLost(item);
            if (toGraph)
            {
                return new(GraphTasks.MoveTo(item, target, zone), lost);
            }
            var fields = GraphTaskMapper.Read(item, zone);
            if (item.IsRecurring && fields.Repeat is null)
            {
                throw new EditRefusedException("This task's repeat pattern can't be moved to a Radicale list.");
            }
            if (JsonNode.Parse(item.RawPayload)?["categories"] is JsonArray { Count: > 0 })
            {
                lost.Add("categories");
            }
            return new(CalDavTasks.Create(target, fields, zone, now), lost);
        }
        if (!toGraph)
        {
            return new(CalDavTasks.MoveTo(item, target), []);
        }
        if (CalDavTasks.ReadRepeat(item, zone) is null)
        {
            throw new EditRefusedException("This task's repeat pattern can't be moved to Microsoft To Do.");
        }
        var read = CalDavTasks.Read(item, zone);
        return new(GraphTasks.Create(target, read with { DueTime = null }, zone), CalDavLost(item, read));
    }

    /// <summary>
    /// Sync does not download a To Do task's checklist steps, files or linked items, so whether it has any is unknown
    /// offline: the line is always listed, and every move from To Do confirms.
    /// </summary>
    private static List<string> GraphLost(Item item) => ["Checklist steps, files and linked items, if the task has any"];

    // What the task editor and To Do map; any other VTODO property is lost.
    private static readonly HashSet<string> Mapped = new(StringComparer.OrdinalIgnoreCase)
    {
        "UID", "DTSTAMP", "CREATED", "LAST-MODIFIED", "SEQUENCE", "SUMMARY", "DESCRIPTION", "DUE", "DTSTART", "PRIORITY", "STATUS",
        "COMPLETED", "PERCENT-COMPLETE", "RRULE", "RDATE", "CATEGORIES",
    };

    private static List<string> CalDavLost(Item item, TaskFields fields)
    {
        var calendar = CustomTimeZones.Load(item.RawPayload)!;
        var todo = CalDavTasks.Master(calendar);
        var lost = new List<string>();
        if (fields.DueTime is not null)
        {
            lost.Add("the due time");
        }
        var others = todo.Alarms.Count - (fields.Reminder is null ? 0 : 1);
        if (others > 0)
        {
            lost.Add(others == 1 ? "1 other reminder" : $"{others} other reminders");
        }
        if (todo.PercentComplete is > 0 and < 100 || todo.PercentComplete == 100 && !fields.Completed)
        {
            lost.Add("percent complete");
        }
        if (todo.Categories.Count > 0)
        {
            lost.Add("categories");
        }
        if (todo.Properties.Any(p => !Mapped.Contains(p.Name) && !p.Name.Equals("EXDATE", StringComparison.OrdinalIgnoreCase)))
        {
            lost.Add("other task properties");
        }
        if (todo.Properties.Any(p => p.Name.Equals("EXDATE", StringComparison.OrdinalIgnoreCase)))
        {
            lost.Add("Skipped dates in the repeat");
        }
        if (calendar.Todos.Count > 1)
        {
            lost.Add("Changed occurrences");
        }
        return lost;
    }
}
