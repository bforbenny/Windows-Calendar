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
        var type = payload.StartsWith('{') ? AccountType.MS365 : AccountType.CalDAV;
        Assert.Equal(high, TaskQuery.IsHighImportance(Task("x") with { RawPayload = payload }, type));
    }

    private static string Vtodo(string lines) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:t\r\nBEGIN:VTODO\r\nUID:1\r\nSUMMARY:Pay rent\r\n{lines}END:VTODO\r\nEND:VCALENDAR\r\n";

    [Theory]
    [InlineData("PRIORITY:1\r\n", "high")]
    [InlineData("PRIORITY:4\r\n", "high")]
    [InlineData("PRIORITY:5\r\n", "normal")]
    [InlineData("PRIORITY:0\r\n", "normal")]
    [InlineData("", "normal")]
    [InlineData("PRIORITY:6\r\n", "low")]
    [InlineData("PRIORITY:9\r\n", "low")]
    public void Vtodo_PriorityMapsToImportance(string line, string importance)
    {
        var task = Task("x") with { RawPayload = Vtodo(line) };

        Assert.Equal(importance, TaskQuery.Fields(task, TimeZoneInfo.Utc, AccountType.CalDAV).Importance);
        Assert.Equal(importance == "high", TaskQuery.IsHighImportance(task, AccountType.CalDAV));
    }

    [Fact]
    public void Vtodo_Fields_TitleNotesDueAndReminder()
    {
        var payload = Vtodo("DUE:20261110T150000Z\r\nDESCRIPTION:Bank details\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nDESCRIPTION:r\r\nTRIGGER:-PT30M\r\nEND:VALARM\r\n");
        var due = new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);
        var task = Task("x") with { Summary = "Pay rent", RawPayload = payload, DueUtc = due, AllDay = false };

        var fields = TaskQuery.Fields(task, TimeZoneInfo.Utc, AccountType.CalDAV);

        Assert.Equal(("Pay rent", false, new DateOnly(2026, 11, 10), "Bank details"), (fields.Title, fields.Completed, fields.Due, fields.Notes));
        Assert.Equal(new DateTime(2026, 11, 10, 14, 30, 0), fields.Reminder);
        Assert.Equal(new TimeOnly(15, 0), TaskQuery.DueTime(task, TimeZoneInfo.Utc));
        Assert.Null(TaskQuery.DueTime(task with { AllDay = true }, TimeZoneInfo.Utc));
    }

    [Fact]
    public void HighImportance_IsReadAgainWhenThePayloadChanges()
    {
        var task = Task("cache") with { RawPayload = """{"importance":"normal"}""" };

        Assert.False(TaskQuery.IsHighImportance(task, AccountType.MS365));
        Assert.True(TaskQuery.IsHighImportance(task with { RawPayload = """{"importance":"high"}""" }, AccountType.MS365));
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
    private static readonly HashSet<string> Lists = ["c1"];

    private static Item Done(string id, DateTimeOffset? completed, string list = "c1", bool recurring = false, bool open = false) =>
        new(id, list, ItemKind.Task, null, "{}", Summary: id, CompletedUtc: open ? null : completed, IsRecurring: recurring);

    private static string[] Old(params Item[] tasks) =>
        TaskQuery.OldCompleted(tasks, Now, Lists).Select(t => t.Id).ToArray();

    [Fact]
    public void OldCompleted_OlderThan365Days_Included() =>
        Assert.Equal(["old"], Old(Done("old", Now.AddDays(-365).AddSeconds(-1))));

    [Fact]
    public void OldCompleted_ExactlyAtOrNewer_Excluded() =>
        Assert.Empty(Old(Done("at", Now.AddDays(-365)), Done("new", Now.AddDays(-10))));

    [Fact]
    public void OldCompleted_OpenTask_Excluded() =>
        Assert.Empty(Old(Done("open", Now.AddDays(-800), open: true)));

    [Fact]
    public void OldCompleted_Recurring_Excluded() =>
        Assert.Empty(Old(Done("rec", Now.AddDays(-800), recurring: true)));

    [Fact]
    public void OldCompleted_NonEligibleList_Excluded() =>
        Assert.Empty(Old(Done("other", Now.AddDays(-800), list: "flagged")));
}
