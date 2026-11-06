using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Core.Tests;

/// <summary>Tasks view filters, sorting and importance (tasks interim).</summary>
public sealed class TaskQueryTests
{
    private static readonly DateOnly Today = new(2026, 11, 6);

    private static Item Task(string title, DateOnly? due = null, bool completed = false) =>
        new(title, "c1", ItemKind.Task, null, "{}", Summary: title,
            DueUtc: due is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
            AllDay: due is not null, Status: completed ? "COMPLETED" : "NEEDS-ACTION");

    private static readonly Item[] Tasks =
    [
        Task("b undated"),
        Task("a undated"),
        Task("due today", Today),
        Task("overdue", Today.AddDays(-2)),
        Task("later", Today.AddDays(3)),
        Task("next week", Today.AddDays(7)),
        Task("done", Today, completed: true),
    ];

    private static string[] Titles(TaskFilter filter) =>
        TaskQuery.Apply(Tasks, filter, Today, TimeZoneInfo.Utc).Select(t => t.Summary!).ToArray();

    [Fact]
    public void All_IsOpenTasks_ByDueDate_UndatedLastByTitle() =>
        Assert.Equal(["overdue", "due today", "later", "next week", "a undated", "b undated"], Titles(TaskFilter.All));

    [Fact]
    public void Today_Overdue_Completed()
    {
        Assert.Equal(["due today"], Titles(TaskFilter.Today));
        Assert.Equal(["due today", "later"], Titles(TaskFilter.DueIn7Days));
        Assert.Equal(["overdue"], Titles(TaskFilter.Overdue));
        Assert.Equal(["done"], Titles(TaskFilter.Completed));
    }

    [Fact]
    public void CalDavTask_IsIndexedOnItsDueDate_NotItsStart()
    {
        var due = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var dateOnly = Task("x") with { StartUtc = due.AddDays(-5), DueUtc = due, AllDay = true };
        var timed = dateOnly with { DueUtc = due.AddHours(15), AllDay = false };
        var window = (From: due.AddDays(-30), To: due.AddDays(30));

        Assert.Equal([(due, due.AddDays(1), due)],
            OccurrenceExpander.Expand(dateOnly, AccountType.CalDAV, window.From, window.To, TimeZoneInfo.Utc));
        Assert.Equal([(due.AddHours(15), due.AddHours(15), due.AddHours(15))],
            OccurrenceExpander.Expand(timed, AccountType.CalDAV, window.From, window.To, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData("""{"title":"x","importance":"high"}""", true)]
    [InlineData("""{"title":"x","importance":"normal"}""", false)]
    [InlineData("PRIORITY:2", true)]
    [InlineData("PRIORITY:5", false)]
    public void HighImportance_FromToDoOrVtodoPriority(string payload, bool high)
    {
        if (payload.StartsWith("PRIORITY", StringComparison.Ordinal))
        {
            payload = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:t\r\nBEGIN:VTODO\r\nUID:1\r\n{payload}\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        }
        Assert.Equal(high, TaskQuery.IsHighImportance(Task("x") with { RawPayload = payload }));
    }
}
