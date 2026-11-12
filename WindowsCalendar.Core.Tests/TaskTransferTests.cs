using System.Text.Json;
using System.Text.Json.Nodes;
using Ical.Net;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Sync;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Core.Tests;

/// <summary>Moving tasks between lists (tasks R11) and the remembered list of the "New task" box (tasks R12).</summary>
public sealed class TaskTransferTests : IDisposable
{
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 1, 0, 0, TimeSpan.Zero);
    private static readonly Collection Radicale = new("r1", "a1", CollectionKind.TaskList, "Tasks", "Navy", "Circle", true, false, "https://dav.example.test/ben/tasks/");
    private static readonly Collection Radicale2 = new("r2", "a1", CollectionKind.TaskList, "Chores", "Teal", "Square", true, false, "https://dav.example.test/ben/chores/");
    private static readonly Collection Todo = new("g1", "a2", CollectionKind.TaskList, "Tasks", "Red", "Circle", true, false, "list1");

    private readonly string _dir = Directory.CreateTempSubdirectory("wincal-taskmove-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Item CalDav(string body) =>
        CalDavItemMapper.Map("https://dav.example.test/ben/tasks/t.ics", "\"e1\"",
            ("BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Test//EN\n" + body + "END:VCALENDAR\n").Replace("\n", "\r\n"), Sydney)!
        with { Id = "i1", CollectionId = "r1" };

    private static Item Graph(string json) => GraphTaskMapper.Map(JsonDocument.Parse(json).RootElement, "g1", Sydney)! with { Id = "i2" };

    private static JsonObject Json(Item item) => JsonNode.Parse(item.RawPayload)!.AsObject();

    private const string Weekly = """
        BEGIN:VTODO
        UID:t1
        DTSTAMP:20260101T000000Z
        SUMMARY:Bins
        DESCRIPTION:Out by 7
        DUE;VALUE=DATE:20261112
        DTSTART;VALUE=DATE:20261112
        RRULE:FREQ=WEEKLY;BYDAY=TH
        PRIORITY:2
        END:VTODO

        """;

    private const string Rich = """
        BEGIN:VTODO
        UID:t2
        DTSTAMP:20260101T000000Z
        SUMMARY:Pay rent
        DESCRIPTION:Before the 5th
        DUE;TZID=Australia/Sydney:20261110T170000
        PRIORITY:7
        PERCENT-COMPLETE:40
        CATEGORIES:Home
        URL:https://example.test/rent
        BEGIN:VALARM
        ACTION:DISPLAY
        TRIGGER:-PT15M
        DESCRIPTION:First
        END:VALARM
        BEGIN:VALARM
        ACTION:AUDIO
        TRIGGER:-PT1H
        END:VALARM
        END:VTODO

        """;

    private const string ChecklistLine = "Checklist steps, files and linked items, if the task has any";

    private const string ToDoJson = """
        {"id":"x1","title":"Book flights","status":"notStarted","importance":"high","body":{"contentType":"text","content":"Perth"},
         "dueDateTime":{"dateTime":"2026-11-20T00:00:00","timeZone":"UTC"},"isReminderOn":true,
         "reminderDateTime":{"dateTime":"2026-11-19T22:00:00","timeZone":"UTC"},
         "checklistItems":[{"displayName":"Passport"}],"linkedResources":[{"displayName":"Mail"}],"categories":["Travel"]}
        """;

    [Fact]
    public void ToDoToRadicale_MapsFields_AndListsWhatIsLost()
    {
        var result = TaskTransfer.Move(Graph(ToDoJson), true, Radicale, false, Sydney, Now);

        var read = CalDavTasks.Read(result.Moved, Sydney);
        Assert.Equal(("Book flights", "Perth", new DateOnly(2026, 11, 20), "high", false), (read.Title, read.Notes, read.Due, read.Importance, read.Completed));
        Assert.NotNull(read.Reminder);
        Assert.Equal("r1", result.Moved.CollectionId);
        Assert.EndsWith(".ics", result.Moved.RemoteId);
        Assert.Equal([ChecklistLine, "categories"], result.Lost);
    }

    [Fact]
    public void ToDoToRadicale_AlwaysListsTheChecklistLine()
    {
        var result = TaskTransfer.Move(Graph("""{"id":"x2","title":"Plain","status":"completed","importance":"low"}"""), true, Radicale, false, Sydney, Now);

        Assert.Equal([ChecklistLine], result.Lost);
        var read = CalDavTasks.Read(result.Moved, Sydney);
        Assert.True(read.Completed);
        Assert.Equal("low", read.Importance);
    }

    [Fact]
    public void RadicaleToToDo_MapsPriorityBand_AndListsWhatIsLost()
    {
        var result = TaskTransfer.Move(CalDav(Rich), false, Todo, true, Sydney, Now);

        var json = Json(result.Moved);
        Assert.Equal("low", json["importance"]!.GetValue<string>());
        Assert.Equal("Pay rent", json["title"]!.GetValue<string>());
        Assert.Equal("Before the 5th", json["body"]!["content"]!.GetValue<string>());
        Assert.Equal("2026-11-10", GraphTaskMapper.Read(result.Moved, Sydney).Due!.Value.ToString("yyyy-MM-dd"));
        Assert.True(json["isReminderOn"]!.GetValue<bool>());
        Assert.Equal("g1", result.Moved.CollectionId);
        Assert.Null(result.Moved.RemoteId);
        Assert.Equal(["the due time", "1 other reminder", "percent complete", "categories", "other task properties"], result.Lost);
    }

    [Fact]
    public void RadicaleToToDo_ListsNothingWhenNothingIsLost()
    {
        var result = TaskTransfer.Move(CalDav(Weekly), false, Todo, true, Sydney, Now);

        Assert.Empty(result.Lost);
        Assert.Equal("high", Json(result.Moved)["importance"]!.GetValue<string>());
    }

    [Fact]
    public void WeeklyRepeat_SurvivesBothDirections()
    {
        var toDo = TaskTransfer.Move(CalDav(Weekly), false, Todo, true, Sydney, Now).Moved;
        var rule = GraphTaskMapper.Read(toDo, Sydney).Repeat!;
        Assert.Equal((RepeatFrequency.Weekly, 1), (rule.Frequency, rule.Interval));
        Assert.Equal([DayOfWeek.Thursday], rule.Weekdays);

        var back = TaskTransfer.Move(toDo with { Id = "i3" }, true, Radicale, false, Sydney, Now).Moved;
        var rrule = Calendar.Load(back.RawPayload)!.Todos.Single().RecurrenceRule!;
        Assert.Equal(("WEEKLY", 1), (rrule.Frequency.ToString().ToUpperInvariant(), rrule.Interval));
        var again = CalDavTasks.Read(back, Sydney).Repeat!;
        Assert.Equal(RepeatFrequency.Weekly, again.Frequency);
        Assert.Equal([DayOfWeek.Thursday], again.Weekdays);
        Assert.Equal(new DateOnly(2026, 11, 12), CalDavTasks.Read(back, Sydney).Due);
    }

    [Fact]
    public void InexpressibleRepeat_IsRefused_BothWays()
    {
        var odd = CalDav("""
            BEGIN:VTODO
            UID:t3
            DTSTAMP:20260101T000000Z
            SUMMARY:Odd
            DUE;VALUE=DATE:20261112
            RRULE:FREQ=YEARLY;BYMONTH=1,6;BYMONTHDAY=3
            END:VTODO

            """);
        Assert.Throws<EditRefusedException>(() => TaskTransfer.Move(odd, false, Todo, true, Sydney, Now));

        var graph = Graph("""{"id":"x3","title":"Odd","status":"notStarted","recurrence":{"pattern":{"type":"hourly"}},"dueDateTime":{"dateTime":"2026-11-12T00:00:00","timeZone":"UTC"}}""");
        Assert.True(graph.IsRecurring);
        Assert.Throws<EditRefusedException>(() => TaskTransfer.Move(graph, true, Radicale, false, Sydney, Now));
    }

    [Fact]
    public void SameAccountRadicale_KeepsUid_NewIdAndHref()
    {
        var item = CalDav(Weekly);

        var result = TaskTransfer.Move(item, false, Radicale2, false, Sydney, Now);

        Assert.Empty(result.Lost);
        Assert.NotEqual(item.Id, result.Moved.Id);
        Assert.Equal("r2", result.Moved.CollectionId);
        Assert.Equal("https://dav.example.test/ben/chores/t1.ics", result.Moved.RemoteId);
        Assert.Null(result.Moved.Etag);
        Assert.Equal(item.RawPayload, result.Moved.RawPayload);
    }

    [Fact]
    public void SameAccountToDo_GetsNewId_AndCopiesWritableFields()
    {
        var item = Graph(ToDoJson);
        var other = Todo with { Id = "g2", RemoteRef = "list2" };

        var result = TaskTransfer.Move(item, true, other, true, Sydney, Now);

        Assert.NotEqual(item.Id, result.Moved.Id);
        Assert.Null(result.Moved.RemoteId);
        Assert.Equal("g2", result.Moved.CollectionId);
        var json = Json(result.Moved);
        Assert.Equal("Book flights", json["title"]!.GetValue<string>());
        Assert.Null(json["id"]);
        Assert.Null(json["checklistItems"]);
        Assert.Equal([ChecklistLine], result.Lost);
    }

    [Fact]
    public void MoveItem_QueuesCreateThenDeleteAsOneLinkedGroup()
    {
        using var store = LocalStore.Open(Path.Combine(_dir, "store.db"));
        store.AddAccount(new Account("a1", AccountType.CalDAV, "Radicale"));
        store.AddCollection(Radicale);
        store.AddCollection(Radicale2);
        var item = CalDavTasks.Create(Radicale, new TaskFields("Bins", false, new DateOnly(2026, 11, 12), "normal", null), Sydney, Now);
        store.CreateItem(item);
        store.CompleteOutboxWrite(store.GetOutbox()[0].Id, "\"e1\"");

        var moved = TaskTransfer.Move(store.GetItem(item.Id)!, false, Radicale2, false, Sydney, Now).Moved;
        var id = store.MoveItem(item.Id, moved);

        Assert.Equal(moved.Id, id);
        Assert.Null(store.GetItem(item.Id));
        var entries = store.GetOutbox();
        Assert.Equal([(OutboxOperation.Create, id), (OutboxOperation.Delete, item.Id)], entries.Select(e => (e.Operation, e.ItemId)));
        Assert.Equal(entries[0].GroupId, entries[1].GroupId);
        Assert.NotNull(entries[0].GroupId);
        Assert.Equal("Bins", store.GetItem(id)!.Summary);
    }

    [Fact]
    public void NewTaskList_IsRemembered_InTheStore()
    {
        using var store = LocalStore.Open(Path.Combine(_dir, "store2.db"));
        Assert.Null(store.NewTaskListId);

        store.SetNewTaskListId("r2");

        Assert.Equal("r2", store.NewTaskListId);
    }

    [Theory]
    [InlineData("r2", "r1", "r2")]
    [InlineData("gone", "r1", "r1")]
    [InlineData(null, "r1", "r1")]
    [InlineData("gone", "also-gone", "r1")]
    public void PickNewTaskList_FallsBackToDefaultThenFirstWritable(string? remembered, string? defaultId, string expected) =>
        Assert.Equal(expected, TaskQuery.PickNewTaskList(remembered, defaultId, ["r1", "r2"]));

    [Fact]
    public void PickNewTaskList_NoWritableList_IsNull() => Assert.Null(TaskQuery.PickNewTaskList("r1", "r1", []));

    [Fact]
    public void RadicaleToToDo_ListsSkippedDatesAndChangedOccurrences()
    {
        var skipped = CalDav(Weekly.Replace("RRULE:FREQ=WEEKLY;BYDAY=TH", "RRULE:FREQ=WEEKLY;BYDAY=TH\nEXDATE;VALUE=DATE:20261119"));
        Assert.Equal(["Skipped dates in the repeat"], TaskTransfer.Move(skipped, false, Todo, true, Sydney, Now).Lost);

        var overridden = CalDav(Weekly + """
            BEGIN:VTODO
            UID:t1
            DTSTAMP:20260101T000000Z
            RECURRENCE-ID;VALUE=DATE:20261119
            SUMMARY:Bins early
            DUE;VALUE=DATE:20261118
            END:VTODO

            """);
        Assert.Equal(["Changed occurrences"], TaskTransfer.Move(overridden, false, Todo, true, Sydney, Now).Lost);
    }
}
