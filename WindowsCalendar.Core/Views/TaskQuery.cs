using System.Text.Json;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Views;

/// <summary>The Tasks view filters (tasks interim): All is the open tasks; DueIn7Days is open tasks due today to 6 days ahead.</summary>
public enum TaskFilter { All, Today, DueIn7Days, Overdue, Completed }

/// <summary>Filtering and sorting of the Tasks view (tasks R1 interim).</summary>
public static class TaskQuery
{
    public static bool IsCompleted(Item task) =>
        task.CompletedUtc is not null || string.Equals(task.Status, "completed", StringComparison.OrdinalIgnoreCase);

    /// <summary>The day the task is due, in <paramref name="zone"/>; null when it has no due date.</summary>
    public static DateOnly? DueDay(Item task, TimeZoneInfo zone) =>
        task.DueUtc is { } d ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(d, zone).DateTime) : null;

    /// <summary>High importance: To Do <c>importance</c> high, or VTODO PRIORITY 1-4 (tasks R4).</summary>
    public static bool IsHighImportance(Item task)
    {
        try
        {
            if (task.RawPayload.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(task.RawPayload);
                return Graph.GraphClient.Text(doc.RootElement, "importance") == "high";
            }
            return Ical.Net.Calendar.Load(task.RawPayload)?.Todos.FirstOrDefault()?.Priority is >= 1 and <= 4;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The editor's fields for a To Do task or a VTODO (priority per tasks R4).</summary>
    public static Graph.TaskFields Fields(Item task, TimeZoneInfo zone)
    {
        if (task.RawPayload.TrimStart().StartsWith('{'))
        {
            return Graph.GraphTaskMapper.Read(task);
        }
        var todo = Ical.Net.Calendar.Load(task.RawPayload)?.Todos.FirstOrDefault();
        var importance = todo?.Priority switch { >= 1 and <= 4 => "high", >= 6 and <= 9 => "low", _ => "normal" };
        return new Graph.TaskFields(task.Summary ?? "", IsCompleted(task), DueDay(task, zone), importance, todo?.Description);
    }

    /// <summary>The tasks the filter shows, by due date (undated last), then title.</summary>
    public static IReadOnlyList<Item> Apply(IEnumerable<Item> tasks, TaskFilter filter, DateOnly today, TimeZoneInfo zone) =>
        tasks
            .Where(t => filter switch
            {
                TaskFilter.Completed => IsCompleted(t),
                TaskFilter.Today => !IsCompleted(t) && DueDay(t, zone) == today,
                TaskFilter.DueIn7Days => !IsCompleted(t) && DueDay(t, zone) is { } d && d >= today && d < today.AddDays(7),
                TaskFilter.Overdue => !IsCompleted(t) && DueDay(t, zone) < today,
                _ => !IsCompleted(t),
            })
            .OrderBy(t => DueDay(t, zone) is null)
            .ThenBy(t => DueDay(t, zone))
            .ThenBy(t => t.Summary, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
