using Microsoft.Extensions.Time.Testing;
using WindowsCalendar.Core.Reminders;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>Reminder sources, planning and state (reminders-notifications R1, R2, R4, R5, R7).</summary>
public sealed class ReminderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private readonly string _dir = Directory.CreateTempSubdirectory("wincal-rem-").FullName;
    private readonly FakeTimeProvider _time = new(Now);
    private readonly LocalStore _store;

    public ReminderTests()
    {
        _time.SetLocalTimeZone(Utc);
        _store = LocalStore.Open(Path.Combine(_dir, "store.db"), _time);
        _store.AddAccount(new Account("rad", AccountType.CalDAV, "Radicale"));
        _store.AddCollection(new Collection("cal", "rad", CollectionKind.Calendar, "Home", "Navy", "Circle", true, false, "https://example.test/cal/"));
        _store.AddCollection(new Collection("quiet", "rad", CollectionKind.Calendar, "Holidays", "Navy", "Circle", false, false, "https://example.test/hol/"));
        _store.AddCollection(new Collection("todo", "rad", CollectionKind.TaskList, "Tasks", "Navy", "Circle", true, false, "https://example.test/todo/"));
        _store.AddAccount(new Account("m365", AccountType.MS365, "Work"));
        _store.AddCollection(new Collection("work", "m365", CollectionKind.Calendar, "Calendar", "Navy", "Circle", true, false, "AAMk"));
        _store.AddCollection(new Collection("todo365", "m365", CollectionKind.TaskList, "To Do", "Navy", "Circle", true, false, "AAMt"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, TimeSpan.Zero);

    private static string Ics(string component, params string[] parts) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n"
        + string.Concat(parts.Select(p => $"BEGIN:{component}\r\nDTSTAMP:20260101T000000Z\r\n" + p.Replace("\n", "\r\n") + $"END:{component}\r\n"))
        + "END:VCALENDAR\r\n";

    private static string Alarm(string trigger) => $"BEGIN:VALARM\nACTION:DISPLAY\nTRIGGER{trigger}\nEND:VALARM\n";

    private static Reminder R(string id, DateTimeOffset fire, DateTimeOffset? lateUntil) =>
        new(id, "cal", ItemKind.Event, null, fire, id, lateUntil, lateUntil, false, null, "Home", null, lateUntil);

    private IReadOnlyList<Reminder> Collect() => ReminderSource.Collect(_store, Now, Utc);

    // ---- Planner (R2) ----

    [Fact]
    public void Plan_SchedulesOnlyWithinTheWindow()
    {
        var plan = ReminderPlanner.Plan(
            [R("soon", Now.AddMinutes(30), Now.AddHours(1)), R("later", Now.AddHours(3), Now.AddHours(4))],
            new HashSet<string>(), Now, ReminderPlanner.RunningWindow);

        Assert.Equal(["soon"], plan.Schedule.Select(r => r.ItemId));
        Assert.Empty(plan.ShowNow);

        var quit = ReminderPlanner.Plan([R("later", Now.AddHours(3), Now.AddHours(4))], new HashSet<string>(), Now, ReminderPlanner.QuitWindow);
        Assert.Equal(["later"], quit.Schedule.Select(r => r.ItemId));
    }

    [Fact]
    public void Plan_PastReminder_ShowsNowOnlyIfNeverHandledAndNotStarted()
    {
        var late = R("late", Now.AddMinutes(-10), Now.AddMinutes(5));
        var started = R("started", Now.AddMinutes(-20), Now.AddMinutes(-5));
        var handled = R("handled", Now.AddMinutes(-10), Now.AddMinutes(5));
        var undated = R("undated", Now.AddMinutes(-10), null);
        var tooOld = R("tooOld", Now.AddHours(-3), Now.AddDays(1));

        var plan = ReminderPlanner.Plan([late, started, handled, undated, tooOld], new HashSet<string> { handled.Key }, Now, ReminderPlanner.RunningWindow);

        Assert.Equal(["late"], plan.ShowNow.Select(r => r.ItemId));
        Assert.Empty(plan.Schedule);
    }

    // ---- Alarms (R1) ----

    [Fact]
    public void CalDavEvent_RelativeToStartAndEnd_AndAbsolute()
    {
        _store.CreateItem(new Item("e1", "cal", ItemKind.Event, null, Ics("VEVENT",
            "UID:e1\nDTSTART:20261112T090000Z\nDTEND:20261112T100000Z\nSUMMARY:Review\nLOCATION:Room 1\n"
            + Alarm(":-PT15M") + Alarm(";RELATED=END:-PT5M") + Alarm(";VALUE=DATE-TIME:20261112T070000Z")
            + "BEGIN:VALARM\nACTION:AUDIO\nTRIGGER:-PT1M\nEND:VALARM\n"), Summary: "Review", Location: "Room 1"));

        var reminders = Collect();

        Assert.Equal([At(12, 7), At(12, 8, 45), At(12, 9, 55)], reminders.Select(r => r.FireAt).Order());
        var first = reminders[0];
        Assert.Equal(("Review", "Room 1", "Home", At(12, 9)), (first.Title, first.Location, first.CalendarName, first.ShowLateUntil));
    }

    [Fact]
    public void CalDavSeries_OneReminderPerOccurrence_OverrideUsesItsOwnAlarm()
    {
        _store.CreateItem(new Item("s1", "cal", ItemKind.Event, null, Ics("VEVENT",
            "UID:s1\nDTSTART:20261112T090000Z\nDURATION:PT1H\nRRULE:FREQ=DAILY;COUNT=3\n"
            + Alarm(":-PT10M") + Alarm(";VALUE=DATE-TIME:20261112T060000Z"),
            "UID:s1\nRECURRENCE-ID:20261113T090000Z\nDTSTART:20261113T110000Z\nDURATION:PT1H\n" + Alarm(":-PT30M")), IsRecurring: true));

        var fires = Collect().Select(r => r.FireAt).Order();

        // The absolute trigger on the repeating master is skipped.
        Assert.Equal([At(12, 8, 50), At(13, 10, 30), At(14, 8, 50)], fires);
    }

    [Fact]
    public void GraphEvent_ReminderOnOff_AndOccurrenceFallsBackToItsSeries()
    {
        _store.CreateItem(new Item("g1", "work", ItemKind.Event, "AAMk1",
            """{"isReminderOn":true,"reminderMinutesBeforeStart":15}""", Summary: "Sync", StartUtc: At(12, 10), EndUtc: At(12, 11),
            JoinUrl: "https://teams.microsoft.com/l/meetup-join/x"));
        _store.CreateItem(new Item("g2", "work", ItemKind.Event, "AAMk2",
            """{"isReminderOn":false,"reminderMinutesBeforeStart":15}""", Summary: "Off", StartUtc: At(12, 12), EndUtc: At(12, 13)));
        _store.CreateItem(new Item("occ", "work", ItemKind.Event, "AAMk3occ", "{}", Summary: "Weekly",
            StartUtc: At(13, 9), EndUtc: At(13, 10), SeriesId: "AAMk3"));
        _store.CreateItem(new Item("master", "work", ItemKind.Event, "AAMk3",
            """{"type":"seriesMaster","isReminderOn":true,"reminderMinutesBeforeStart":60}""", IsRecurring: true));

        var reminders = Collect().OrderBy(r => r.FireAt).ToList();

        Assert.Equal([("g1", At(12, 9, 45)), ("occ", At(13, 8))], reminders.Select(r => (r.ItemId, r.FireAt)));
        Assert.Equal("https://teams.microsoft.com/l/meetup-join/x", reminders[0].JoinUrl);
    }

    [Fact]
    public void Tasks_GraphReminderAndCalDavAlarm_CompletedHaveNone()
    {
        _store.CreateItem(new Item("t1", "todo365", ItemKind.Task, "AAMt1",
            """{"title":"Pay","isReminderOn":true,"reminderDateTime":{"dateTime":"2026-11-12T09:30:00.0000000","timeZone":"UTC"},"dueDateTime":{"dateTime":"2026-11-12T00:00:00.0000000","timeZone":"UTC"}}""",
            Summary: "Pay"));
        _store.CreateItem(new Item("t2", "todo365", ItemKind.Task, "AAMt2",
            """{"title":"Done","status":"completed","isReminderOn":true,"reminderDateTime":{"dateTime":"2026-11-12T09:30:00.0000000","timeZone":"UTC"}}""",
            Summary: "Done", Status: "completed"));
        _store.CreateItem(new Item("t3", "todo", ItemKind.Task, null, Ics("VTODO",
            "UID:t3\nDUE:20261112T170000Z\nSUMMARY:Report\n" + Alarm(";RELATED=END:-PT1H")),
            Summary: "Report", DueUtc: At(12, 17)));

        var reminders = Collect().OrderBy(r => r.FireAt).ToList();

        Assert.Equal([("t1", At(12, 9, 30), At(13, 0)), ("t3", At(12, 16), At(12, 17))],
            reminders.Select(r => (r.ItemId, r.FireAt, r.ShowLateUntil!.Value)));
        Assert.All(reminders, r => Assert.Equal(ItemKind.Task, r.Kind));
    }

    // ---- Calendar filters (R5) ----

    [Fact]
    public void CalendarsWithRemindersOff_OrHidden_HaveNone()
    {
        var ev = Ics("VEVENT", "UID:x\nDTSTART:20261112T090000Z\nDTEND:20261112T100000Z\n" + Alarm(":-PT15M"));
        _store.CreateItem(new Item("quiet1", "quiet", ItemKind.Event, null, ev));
        _store.CreateItem(new Item("shown", "cal", ItemKind.Event, null, ev));
        Assert.Equal(["shown"], Collect().Select(r => r.ItemId));

        _store.SetCollectionHidden("cal", true);
        Assert.Empty(Collect());
    }

    // ---- State (R2, R4) ----

    [Fact]
    public void State_RemembersHandledKeys_AndPrunesOldOnes()
    {
        var old = R("old", Now.AddDays(-10), Now.AddDays(-9));
        var recent = R("recent", Now.AddDays(-10), Now.AddDays(1));
        _store.MarkRemindersScheduled([old, recent, recent]);

        Assert.Equal(new HashSet<string> { old.Key, recent.Key }, _store.GetHandledReminderKeys());

        // The occurrence still ahead keeps its row, so its late reminder never shows twice.
        _store.PruneReminderState(Now.AddDays(-7));
        Assert.Equal(new HashSet<string> { recent.Key }, _store.GetHandledReminderKeys());
    }
}
