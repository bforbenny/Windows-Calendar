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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Payload, bool High)> HighCache = new();

    /// <summary>
    /// High importance: To Do <c>importance</c> high, or VTODO PRIORITY 1-4 (tasks R4). The backend is the account's
    /// <paramref name="type"/>. Read once per task version: the answer is kept per item until its payload changes.
    /// </summary>
    public static bool IsHighImportance(Item task, AccountType type)
    {
        if (HighCache.TryGetValue(task.Id, out var cached) && cached.Payload == task.RawPayload)
        {
            return cached.High;
        }
        var high = ReadHighImportance(task, type);
        HighCache[task.Id] = (task.RawPayload, high);
        return high;
    }

    private static bool ReadHighImportance(Item task, AccountType type)
    {
        try
        {
            if (type == AccountType.MS365)
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

    /// <summary>
    /// The editor's fields for a To Do task or a VTODO (priority per tasks R4; a VTODO's due time and repeat rule are
    /// read too, see <see cref="CalDav.CalDavTasks.Read"/>).
    /// </summary>
    public static Graph.TaskFields Fields(Item task, TimeZoneInfo zone, AccountType type)
    {
        if (type == AccountType.MS365)
        {
            return Graph.GraphTaskMapper.Read(task, zone);
        }
        return CalDav.CalDavTasks.Read(task, zone);
    }

    /// <summary>The time of day a VTODO is due, in <paramref name="zone"/>; null for a date-only or undated task (display only).</summary>
    public static TimeOnly? DueTime(Item task, TimeZoneInfo zone) =>
        task.DueUtc is { } d && !task.AllDay ? TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(d, zone).DateTime) : null;

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

    /// <summary>Completed tasks older than this are offered for deletion (tasks R10).</summary>
    public static readonly TimeSpan OldCompletedAge = TimeSpan.FromDays(365);

    /// <summary>
    /// The completed tasks in <paramref name="listIds"/> (the writable To Do lists) whose completion time is more than
    /// 365 days before <paramref name="now"/>; repeating tasks are skipped (tasks R10).
    /// </summary>
    public static IReadOnlyList<Item> OldCompleted(IEnumerable<Item> tasks, DateTimeOffset now, ISet<string> listIds) =>
        tasks
            .Where(t => t.Kind == ItemKind.Task && !t.IsRecurring && listIds.Contains(t.CollectionId)
                && t.CompletedUtc is { } done && done < now - OldCompletedAge)
            .ToList();

    /// <summary>
    /// The list the "New task" box starts on (tasks R12): the remembered one while it is still a writable task list, else
    /// the default task list, else the first writable one; null when there is none.
    /// </summary>
    public static string? PickNewTaskList(string? remembered, string? defaultId, IReadOnlyList<string> writableIds) =>
        remembered is not null && writableIds.Contains(remembered) ? remembered
        : defaultId is not null && writableIds.Contains(defaultId) ? defaultId
        : writableIds.FirstOrDefault();
}
