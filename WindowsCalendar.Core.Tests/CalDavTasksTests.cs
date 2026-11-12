using Ical.Net;
using Ical.Net.CalendarComponents;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Reminders;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Sync;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Core.Tests;

/// <summary>Radicale task writes (tasks third slice, R7, R9; sync-radicale R8).</summary>
public sealed class CalDavTasksTests
{
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 1, 0, 0, TimeSpan.Zero);
    private static readonly Collection Tasks = new("c1", "a1", CollectionKind.TaskList, "Tasks", "Navy", "Circle", true, false, "https://dav.example.test/ben/tasks/");

    private static string Ics(string body) =>
        ("BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Apple Inc.//macOS//EN\n" + body + "END:VCALENDAR\n").Replace("\n", "\r\n");

    private static Item Existing(string body) =>
        CalDavItemMapper.Map("https://dav.example.test/ben/tasks/t.ics", "\"e1\"", Ics(body), Sydney)! with { Id = "i1", CollectionId = "c1" };

    private static Todo TodoOf(Item item) => Calendar.Load(item.RawPayload)!.Todos.Single();

    private const string Rich = """
        BEGIN:VTODO
        UID:t1
        DTSTAMP:20260101T000000Z
        SUMMARY:Pay rent
        DESCRIPTION:Before the 5th
        DUE;VALUE=DATE:20261110
        PRIORITY:7
        SEQUENCE:2
        X-APPLE-SORT-ORDER:12345
        CATEGORIES:Home
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

    private static TaskFields Fields(Item item) => CalDavTasks.Read(item, Sydney);

    [Fact]
    public void Edit_KeepsUnknownPropertiesAndOtherAlarms()
    {
        var item = Existing(Rich);

        var edited = CalDavTasks.Edit(item, Fields(item) with { Title = "Pay rent now", Reminder = new DateTime(2026, 11, 9, 9, 0, 0) }, Sydney, Now)!;

        Assert.Contains("X-APPLE-SORT-ORDER:12345", edited.RawPayload);
        Assert.Contains("CATEGORIES:Home", edited.RawPayload);
        Assert.Equal(2, TodoOf(edited).Alarms.Count);
        Assert.Contains(TodoOf(edited).Alarms, a => a.Action == "AUDIO");
        Assert.Equal("Pay rent now", edited.Summary);
        Assert.Equal(new DateTime(2026, 11, 9, 9, 0, 0), Fields(edited).Reminder);
        Assert.Null(CalDavTasks.Edit(edited, Fields(edited), Sydney, Now));
    }

    [Fact]
    public void Edit_PriorityBandsAreKeptOrWritten()
    {
        var high = Existing(Rich.Replace("PRIORITY:7", "PRIORITY:3"));

        var renamed = CalDavTasks.Edit(high, Fields(high) with { Title = "x" }, Sydney, Now)!;
        var low = CalDavTasks.Edit(high, Fields(high) with { Importance = "low" }, Sydney, Now)!;
        var back = CalDavTasks.Edit(low, Fields(low) with { Importance = "high" }, Sydney, Now)!;
        var normal = CalDavTasks.Edit(low, Fields(low) with { Importance = "normal" }, Sydney, Now)!;

        Assert.Equal(3, TodoOf(renamed).Priority);
        Assert.Equal(9, TodoOf(low).Priority);
        Assert.Equal(1, TodoOf(back).Priority);
        Assert.Equal(5, TodoOf(normal).Priority);
        Assert.Equal("low", Fields(low).Importance);
        Assert.Null(CalDavTasks.Edit(high, Fields(high) with { Importance = "high" }, Sydney, Now));
    }

    [Fact]
    public void Edit_DueIsDateOnlyOrTimedInTheWindowsZone()
    {
        var item = Existing(Rich);

        var timed = CalDavTasks.Edit(item, Fields(item) with { DueTime = new TimeOnly(14, 30) }, Sydney, Now)!;

        Assert.Contains("DUE;TZID=Australia/Sydney:20261110T143000", timed.RawPayload);
        Assert.False(timed.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 3, 30, 0, TimeSpan.Zero), timed.DueUtc);
        Assert.Equal((new DateOnly(2026, 11, 10), new TimeOnly(14, 30)), (Fields(timed).Due!.Value, Fields(timed).DueTime!.Value));

        var dateOnly = CalDavTasks.Edit(timed, Fields(timed) with { Due = new DateOnly(2026, 11, 12), DueTime = null }, Sydney, Now)!;

        Assert.Contains("DUE;VALUE=DATE:20261112", dateOnly.RawPayload);
        Assert.True(dateOnly.AllDay);
        Assert.Null(Fields(dateOnly).DueTime);

        var cleared = CalDavTasks.Edit(dateOnly, Fields(dateOnly) with { Due = null }, Sydney, Now)!;

        Assert.DoesNotContain("DUE", cleared.RawPayload);
        Assert.Null(cleared.DueUtc);
    }

    [Fact]
    public void Edit_MovingDueKeepsTheDistanceToDtstart()
    {
        var item = Existing(Rich.Replace("DUE;VALUE=DATE:20261110", "DTSTART;VALUE=DATE:20261105\nDUE;VALUE=DATE:20261110"));

        var moved = CalDavTasks.Edit(item, Fields(item) with { Due = new DateOnly(2026, 11, 20) }, Sydney, Now)!;

        Assert.Contains("DTSTART;VALUE=DATE:20261115", moved.RawPayload);
        Assert.Contains("DUE;VALUE=DATE:20261120", moved.RawPayload);
    }

    [Fact]
    public void Edit_MovingTimedDueKeepsTheDistanceToDtstart()
    {
        var item = Existing(Rich.Replace("DUE;VALUE=DATE:20261110",
            "DTSTART;TZID=Australia/Sydney:20261110T090000\nDUE;TZID=Australia/Sydney:20261110T170000"));

        var moved = CalDavTasks.Edit(item, Fields(item) with { Due = new DateOnly(2026, 11, 11), DueTime = new TimeOnly(18, 0) }, Sydney, Now)!;

        Assert.Contains("DTSTART;TZID=Australia/Sydney:20261111T100000", moved.RawPayload);
        Assert.Contains("DUE;TZID=Australia/Sydney:20261111T180000", moved.RawPayload);
    }

    [Fact]
    public void CompleteAndReopen_WriteTheCompletionProperties()
    {
        var item = Existing(Rich);

        var done = CalDavTasks.Edit(item, Fields(item) with { Completed = true }, Sydney, Now)!;

        Assert.Contains("STATUS:COMPLETED", done.RawPayload);
        Assert.Contains("COMPLETED:20261105T010000Z", done.RawPayload);
        Assert.Contains("PERCENT-COMPLETE:100", done.RawPayload);
        Assert.Equal(Now, done.CompletedUtc);
        Assert.True(Fields(done).Completed);

        var open = CalDavTasks.Reopen(done, Sydney, Now.AddHours(1));

        Assert.Contains("STATUS:NEEDS-ACTION", open.RawPayload);
        Assert.DoesNotContain("COMPLETED:", open.RawPayload);
        Assert.DoesNotContain("PERCENT-COMPLETE", open.RawPayload);
        Assert.Null(open.CompletedUtc);
        Assert.False(Fields(open).Completed);
    }

    [Fact]
    public void Complete_AdvancesAWeeklyTaskToTheNextDate()
    {
        var item = Existing(Rich.Replace("DUE;VALUE=DATE:20261110", "DTSTART;VALUE=DATE:20261103\nDUE;VALUE=DATE:20261110\nRRULE:FREQ=WEEKLY;BYDAY=TU"));

        var result = CalDavTasks.Complete(item, Sydney, Now);

        Assert.Equal(new DateOnly(2026, 11, 17), result.AdvancedTo);
        Assert.Contains("DUE;VALUE=DATE:20261117", result.Item.RawPayload);
        Assert.Contains("DTSTART;VALUE=DATE:20261110", result.Item.RawPayload);
        Assert.DoesNotContain("COMPLETED:", result.Item.RawPayload);
        Assert.Contains("STATUS:NEEDS-ACTION", result.Item.RawPayload);
        Assert.True(result.Item.IsRecurring);
        Assert.False(TaskQuery.IsCompleted(result.Item));
        Assert.Equal(item.Id, result.Item.Id);
    }

    [Fact]
    public void Complete_AdvancesATimedTaskWithoutDtstart()
    {
        var item = Existing(Rich.Replace("DUE;VALUE=DATE:20261110", "DUE;TZID=Australia/Sydney:20261110T170000\nRRULE:FREQ=DAILY;INTERVAL=2"));

        var result = CalDavTasks.Complete(item, Sydney, Now);

        Assert.Equal(new DateOnly(2026, 11, 12), result.AdvancedTo);
        Assert.Contains("DUE;TZID=Australia/Sydney:20261112T170000", result.Item.RawPayload);
        Assert.DoesNotContain("DTSTART", result.Item.RawPayload);
    }

    [Fact]
    public void Complete_CountExhaustedCompletesTheTask()
    {
        var item = Existing(Rich.Replace("DUE;VALUE=DATE:20261110", "DTSTART;VALUE=DATE:20261103\nDUE;VALUE=DATE:20261110\nRRULE:FREQ=WEEKLY;COUNT=2"));

        var first = CalDavTasks.Complete(item, Sydney, Now);

        Assert.Equal(new DateOnly(2026, 11, 17), first.AdvancedTo);
        Assert.Contains("COUNT=1", first.Item.RawPayload);

        var last = CalDavTasks.Complete(first.Item, Sydney, Now);

        Assert.Null(last.AdvancedTo);
        Assert.Contains("STATUS:COMPLETED", last.Item.RawPayload);
        Assert.Contains("DUE;VALUE=DATE:20261117", last.Item.RawPayload);
        Assert.True(TaskQuery.IsCompleted(last.Item));
    }

    [Fact]
    public void Complete_UntilPassedCompletesAndNoRepeatCompletes()
    {
        var ended = Existing(Rich.Replace("DUE;VALUE=DATE:20261110", "DUE;VALUE=DATE:20261110\nRRULE:FREQ=WEEKLY;UNTIL=20261112"));
        var single = Existing(Rich);

        Assert.Null(CalDavTasks.Complete(ended, Sydney, Now).AdvancedTo);
        Assert.True(TaskQuery.IsCompleted(CalDavTasks.Complete(ended, Sydney, Now).Item));
        Assert.Null(CalDavTasks.Complete(single, Sydney, Now).AdvancedTo);
        Assert.True(TaskQuery.IsCompleted(CalDavTasks.Complete(single, Sydney, Now).Item));
    }

    [Fact]
    public void Edit_IncrementsSequenceAndStamps()
    {
        var item = Existing(Rich);

        var edited = CalDavTasks.Edit(item, Fields(item) with { Notes = "Changed" }, Sydney, Now)!;
        var again = CalDavTasks.Edit(edited, Fields(edited) with { Title = "Again" }, Sydney, Now.AddMinutes(5))!;

        Assert.Equal(3, TodoOf(edited).Sequence);
        Assert.Equal(4, TodoOf(again).Sequence);
        Assert.Contains("LAST-MODIFIED:20261105T010500Z", again.RawPayload);
        Assert.Contains("DTSTAMP:20261105T010500Z", again.RawPayload);
        Assert.Equal(Now.AddMinutes(5), again.LastModifiedUtc);
        Assert.Equal((item.Id, item.RemoteId, item.Etag), (again.Id, again.RemoteId, again.Etag));
    }

    [Fact]
    public void Create_MakesANewTaskWithUidAndStamps()
    {
        var created = CalDavTasks.Create(Tasks, new TaskFields("Buy milk", false, new DateOnly(2026, 11, 8), "high", "2 litres",
            Reminder: new DateTime(2026, 11, 8, 8, 0, 0), DueTime: new TimeOnly(17, 0)), Sydney, Now);

        var todo = TodoOf(created);
        Assert.Equal(ItemKind.Task, created.Kind);
        Assert.Equal("c1", created.CollectionId);
        Assert.StartsWith("https://dav.example.test/ben/tasks/", created.RemoteId);
        Assert.EndsWith(Uri.EscapeDataString(todo.Uid!) + ".ics", created.RemoteId);
        Assert.Equal(0, todo.Sequence);
        Assert.Equal(1, todo.Priority);
        Assert.Contains("CREATED:20261105T010000Z", created.RawPayload);
        Assert.Contains("LAST-MODIFIED:20261105T010000Z", created.RawPayload);
        Assert.Contains("DTSTAMP:20261105T010000Z", created.RawPayload);
        var read = Fields(created);
        Assert.Equal(("Buy milk", "2 litres", new DateOnly(2026, 11, 8), new TimeOnly(17, 0), "high", false),
            (read.Title, read.Notes, read.Due, read.DueTime, read.Importance, read.Completed));
        Assert.Equal(new DateTime(2026, 11, 8, 8, 0, 0), read.Reminder);
        Assert.NotEqual(todo.Uid, TodoOf(CalDavTasks.Create(Tasks, read, Sydney, Now)).Uid);
    }

    [Fact]
    public void Reminder_FollowsTheDueDateWhenItMoves()
    {
        var item = Existing(Rich);

        var moved = CalDavTasks.Edit(item, Fields(item) with { Due = new DateOnly(2026, 11, 20) }, Sydney, Now)!;

        Assert.Equal(new DateTime(2026, 11, 19, 23, 45, 0), Fields(moved).Reminder);
    }

    [Fact]
    public void Restore_PutsThePayloadBackAndReDerivesTaskColumns()
    {
        var item = Existing(Rich);
        var done = CalDavTasks.Edit(item, Fields(item) with { Completed = true, Due = new DateOnly(2026, 11, 20) }, Sydney, Now)!;

        var restored = CalDavTasks.Restore(done, item.RawPayload, Sydney);

        Assert.Equal(ItemKind.Task, restored.Kind);
        Assert.Equal(Fields(item), Fields(restored));
        Assert.DoesNotContain("COMPLETED:", restored.RawPayload);
        Assert.Equal(2, TodoOf(restored).Sequence);
        Assert.Null(restored.CompletedUtc);
        Assert.Equal(item.DueUtc, restored.DueUtc);
        Assert.True(restored.AllDay);
        Assert.Equal("Pay rent", restored.Summary);
        Assert.Equal((done.Id, done.CollectionId, done.RemoteId, done.Etag), (restored.Id, restored.CollectionId, restored.RemoteId, restored.Etag));
        Assert.Throws<ArgumentException>(() => CalDavTasks.Restore(done, Ics("BEGIN:VEVENT\nUID:e\nDTSTAMP:20260101T000000Z\nDTSTART:20261101T000000Z\nEND:VEVENT\n"), Sydney));
    }

    [Fact]
    public void Restore_CoalescesWithAnUnsentChangeInTheOutbox()
    {
        using var store = LocalStore.Open(Path.Combine(Directory.CreateTempSubdirectory("wincal-tasks-").FullName, "store.db"));
        store.AddAccount(new Account("a1", AccountType.CalDAV, "Radicale"));
        store.AddCollection(Tasks);
        var tasks = Tasks;
        var created = CalDavTasks.Create(tasks, new TaskFields("Plan", false, new DateOnly(2026, 11, 8), "normal", null), Sydney, Now);
        store.CreateItem(created);
        var edited = CalDavTasks.Edit(created, Fields(created) with { Completed = true }, Sydney, Now)!;
        store.UpdateItem(edited);

        store.UpdateItem(CalDavTasks.Restore(edited, created.RawPayload, Sydney));

        var outbox = store.GetDueOutbox().Where(e => e.ItemId == created.Id).ToList();
        Assert.Single(outbox);
        Assert.Equal(0, TodoOf(new Item("x", "c1", ItemKind.Task, null, outbox[0].Payload!)).Sequence);
        Assert.DoesNotContain("COMPLETED:", outbox[0].Payload);
        Assert.False(TaskQuery.IsCompleted(store.GetItem(created.Id)!));
    }

    [Fact]
    public void Repeat_WriteAndReadRoundTrip()
    {
        var item = Existing(Rich.Replace("DUE;VALUE=DATE:20261110", "DUE;VALUE=DATE:20261110"));
        RepeatRule[] rules =
        [
            new(RepeatFrequency.Daily, 3, [], null, null, null),
            new(RepeatFrequency.Weekly, 2, [DayOfWeek.Tuesday, DayOfWeek.Friday], null, 5, null),
            new(RepeatFrequency.Monthly, 1, [], null, null, new DateOnly(2027, 6, 30)),
            new(RepeatFrequency.Monthly, 1, [], 2, null, null),
            new(RepeatFrequency.Yearly, 1, [], null, null, null),
        ];

        foreach (var rule in rules)
        {
            var set = CalDavTasks.SetRepeat(item, rule, Sydney, Now)!;
            Assert.True(rule.Equals(CalDavTasks.ReadRepeat(set, Sydney)), rule.ToString());
            Assert.True(rule.Equals(Fields(set).Repeat));
            Assert.True(set.IsRecurring);
            Assert.Contains("X-APPLE-SORT-ORDER:12345", set.RawPayload);
        }
    }

    [Fact]
    public void Repeat_ClearAndUnchangedAndWeekdayShift()
    {
        var item = Existing(Rich);
        Assert.Equal(RepeatRule.None, CalDavTasks.ReadRepeat(item, Sydney));
        Assert.Null(Fields(item).Repeat);
        Assert.Null(CalDavTasks.SetRepeat(item, RepeatRule.None, Sydney, Now));

        // 10 Nov 2026 is a Tuesday: a Friday-only rule moves the due date to Friday 13th.
        var weekly = CalDavTasks.SetRepeat(item, new RepeatRule(RepeatFrequency.Weekly, 1, [DayOfWeek.Friday], null, null, null), Sydney, Now)!;
        Assert.Contains("DUE;VALUE=DATE:20261113", weekly.RawPayload);
        Assert.Contains("BYDAY=FR", weekly.RawPayload);
        Assert.Equal(3, TodoOf(weekly).Sequence);

        var cleared = CalDavTasks.SetRepeat(weekly, RepeatRule.None, Sydney, Now)!;
        Assert.DoesNotContain("RRULE", cleared.RawPayload);
        Assert.False(cleared.IsRecurring);

        Assert.Throws<EditRefusedException>(() => CalDavTasks.SetRepeat(
            Existing("BEGIN:VTODO\nUID:u\nDTSTAMP:20260101T000000Z\nSUMMARY:No date\nEND:VTODO\n"),
            new RepeatRule(RepeatFrequency.Daily, 1, [], null, null, null), Sydney, Now));
    }

    [Fact]
    public void Create_WithARepeatWritesTheRule()
    {
        var created = CalDavTasks.Create(Tasks, new TaskFields("Water plants", false, new DateOnly(2026, 11, 8), "normal", null,
            new RepeatRule(RepeatFrequency.Weekly, 1, [DayOfWeek.Sunday], null, null, null)), Sydney, Now);

        Assert.Contains("RRULE:FREQ=WEEKLY;BYDAY=SU", created.RawPayload);
        Assert.True(created.IsRecurring);
        Assert.Equal(RepeatFrequency.Weekly, Fields(created).Repeat!.Frequency);
    }

    [Fact]
    public void TaskQueryFields_FillsDueTimeAndRepeatForCalDav()
    {
        var item = Existing(Rich.Replace("DUE;VALUE=DATE:20261110", "DUE;TZID=Australia/Sydney:20261110T170000\nRRULE:FREQ=DAILY"));

        var fields = TaskQuery.Fields(item, Sydney, AccountType.CalDAV);

        Assert.Equal(new TimeOnly(17, 0), fields.DueTime);
        Assert.Equal(RepeatFrequency.Daily, fields.Repeat!.Frequency);
        Assert.Equal("low", fields.Importance);
        Assert.Equal(new DateTime(2026, 11, 10, 16, 45, 0), fields.Reminder);
    }

    [Fact]
    public void ConflictDifferences_CompareEveryFieldOfARadicaleTask()
    {
        var server = Existing(Rich);
        var local = Existing(Rich).RawPayload.Replace("SUMMARY:Pay rent", "SUMMARY:My rent").Replace("DUE;VALUE=DATE:20261110", "DUE;VALUE=DATE:20261112");

        var diffs = ConflictResolver.Differences(local, server, graph: false, Sydney);

        Assert.Equal(["Title", "Due", "Reminder"], diffs.Select(d => d.Field));
        Assert.Equal("My rent", diffs[0].Yours);
        Assert.Equal("Pay rent", diffs[0].Server);
    }

    [Fact]
    public void Edit_ClearingDueKeepsEveryRelativeReminderAsAnAbsoluteTime()
    {
        var item = Existing(Rich);

        var cleared = CalDavTasks.Edit(item, Fields(item) with { Due = null }, Sydney, Now)!;

        var todo = TodoOf(cleared);
        Assert.Null(todo.Due);
        Assert.All(todo.Alarms, a => Assert.False(a.Trigger!.IsRelative));
        Assert.Equal(2, todo.Alarms.Count);
        Assert.Equal(new DateTime(2026, 11, 9, 23, 45, 0), Fields(cleared).Reminder);
    }

    [Fact]
    public void Reminder_FromDueIsRelatedToEndEvenWithoutDtstart_AndStartTriggersStillRead()
    {
        var item = Existing(Rich);

        var edited = CalDavTasks.Edit(item, Fields(item) with { Reminder = new DateTime(2026, 11, 9, 8, 0, 0) }, Sydney, Now)!;

        Assert.Contains("RELATED=END", edited.RawPayload);
        Assert.Equal(new DateTime(2026, 11, 9, 8, 0, 0), Fields(edited).Reminder);
        var legacy = Existing(Rich.Replace("TRIGGER:-PT15M", "TRIGGER;RELATED=START:-PT15M"));
        Assert.Equal(new DateTime(2026, 11, 9, 23, 45, 0), Fields(legacy).Reminder);
    }

    [Fact]
    public void Complete_CountIncludesExcludedDates()
    {
        var item = Existing(Rich.Replace("DUE;VALUE=DATE:20261110",
            "DTSTART;VALUE=DATE:20261103\nDUE;VALUE=DATE:20261103\nRRULE:FREQ=WEEKLY;COUNT=4\nEXDATE;VALUE=DATE:20261110"));

        var result = CalDavTasks.Complete(item, Sydney, Now);

        Assert.Equal(new DateOnly(2026, 11, 17), result.AdvancedTo);
        Assert.Contains("COUNT=2", result.Item.RawPayload);
    }

    [Fact]
    public void Complete_AdvancesASeriesWhoseDtstartIsFarBeforeDue()
    {
        var item = Existing(Rich.Replace("DUE;VALUE=DATE:20261110", "DTSTART;VALUE=DATE:20000101\nDUE;VALUE=DATE:20261110\nRRULE:FREQ=DAILY"));

        var result = CalDavTasks.Complete(item, Sydney, Now);

        Assert.Equal(new DateOnly(2026, 11, 11), result.AdvancedTo);
        Assert.Contains("DUE;VALUE=DATE:20261111", result.Item.RawPayload);
    }
}
