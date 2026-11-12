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

    private static readonly Dictionary<string, ReminderStatus> NoState = [];

    private static readonly HashSet<string> NoHistory = [];

    // ---- Planner (R2) ----

    [Fact]
    public void Plan_SchedulesOnlyWithinTheWindow()
    {
        var plan = ReminderPlanner.Plan(
            [R("soon", Now.AddMinutes(30), Now.AddHours(1)), R("later", Now.AddHours(3), Now.AddHours(4))],
            NoState, Now, ReminderPlanner.RunningWindow);

        Assert.Equal(["soon"], plan.Schedule.Select(r => r.ItemId));
        Assert.Empty(plan.ShowNow);

        var quit = ReminderPlanner.Plan([R("later", Now.AddHours(3), Now.AddHours(4))], NoState, Now, ReminderPlanner.QuitWindow);
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

        var states = new Dictionary<string, ReminderStatus> { [handled.Key] = new(ReminderStates.Scheduled, null) };
        var plan = ReminderPlanner.Plan([late, started, handled, undated, tooOld], states, Now, ReminderPlanner.RunningWindow);

        Assert.Equal(["late"], plan.ShowNow.Select(r => r.ItemId));
        Assert.Empty(plan.Schedule);
    }

    [Fact]
    public void Plan_DismissedIsNotScheduled_SnoozedIsKeptUntilItsTime()
    {
        var dismissed = R("dismissed", Now.AddMinutes(30), Now.AddHours(1));
        var scheduled = R("scheduled", Now.AddMinutes(40), Now.AddHours(1));
        var snoozed = R("snoozed", Now.AddMinutes(-5), Now.AddHours(1));
        var snoozeOver = R("snoozeOver", Now.AddMinutes(-20), Now.AddHours(1));
        var states = new Dictionary<string, ReminderStatus>
        {
            [dismissed.Key] = new(ReminderStates.Dismissed, null),
            [scheduled.Key] = new(ReminderStates.Scheduled, null),
            [snoozed.Key] = new(ReminderStates.Snoozed, Now.AddMinutes(10)),
            [snoozeOver.Key] = new(ReminderStates.Snoozed, Now.AddMinutes(-10)),
            // A snooze whose item moved: its key is not among the reminders any more, so nothing keeps it.
            ["moved|0|0"] = new(ReminderStates.Snoozed, Now.AddMinutes(10)),
        };

        var plan = ReminderPlanner.Plan([dismissed, scheduled, snoozed, snoozeOver], states, Now, ReminderPlanner.RunningWindow);

        Assert.Equal(["scheduled"], plan.Schedule.Select(r => r.ItemId));
        Assert.Equal([("snoozed", Now.AddMinutes(10))], plan.Snoozed.Select(s => (s.Reminder.ItemId, s.Until)));
        Assert.Empty(plan.ShowNow);
    }

    // ---- Missed summary (R6) ----

    [Fact]
    public void Missed_DueSinceLastWatched_NotShownByWindows_NotActedOn()
    {
        var since = Now.AddHours(-3);
        var missed = R("missed", Now.AddHours(-2), Now.AddHours(-1));
        var never = R("never", Now.AddHours(-1), null);
        var beforeSleep = R("beforeSleep", Now.AddHours(-4), null);
        var shown = R("shown", Now.AddHours(-2), null);
        var dismissed = R("dismissed", Now.AddHours(-2), null);
        var listed = R("listed", Now.AddHours(-2), null);
        var snoozeDue = R("snoozeDue", Now.AddHours(-5), null);
        var ahead = R("ahead", Now.AddMinutes(5), null);
        var states = new Dictionary<string, ReminderStatus>
        {
            [missed.Key] = new(ReminderStates.Scheduled, null),
            [dismissed.Key] = new(ReminderStates.Dismissed, null),
            [listed.Key] = new(ReminderStates.Missed, null),
            [snoozeDue.Key] = new(ReminderStates.Snoozed, Now.AddHours(-1)),
        };

        var result = ReminderPlanner.Missed([missed, never, beforeSleep, shown, dismissed, listed, snoozeDue, ahead], states,
            new HashSet<string> { shown.Key }, since, Now);

        Assert.Equal(["missed", "never", "snoozeDue"], result.Select(r => r.ItemId).Order());
    }

    [Fact]
    public void Missed_LooksBackAtMostTwelveHours()
    {
        var old = R("old", Now.AddHours(-13), null);
        var recent = R("recent", Now.AddHours(-11), null);

        var result = ReminderPlanner.Missed([old, recent], NoState, NoHistory, Now.AddDays(-2), Now);

        Assert.Equal(["recent"], result.Select(r => r.ItemId));
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
    public void CancelledAndDeclinedMeetings_HaveNone()
    {
        _store.CreateItem(new Item("c1", "cal", ItemKind.Event, null, Ics("VEVENT",
            "UID:c1\nDTSTART:20261112T090000Z\nDTEND:20261112T100000Z\nSTATUS:CANCELLED\n" + Alarm(":-PT15M"))));
        _store.CreateItem(new Item("d1", "work", ItemKind.Event, "AAMkD1",
            """{"id":"AAMkD1","isOrganizer":false,"responseStatus":{"response":"declined"},"isReminderOn":true,"reminderMinutesBeforeStart":15}""",
            Summary: "Declined", StartUtc: At(12, 9), EndUtc: At(12, 10)));

        Assert.Empty(Collect());
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
    public void CalendarsWithRemindersOff_HaveNone_HiddenOnesStillDo()
    {
        var ev = Ics("VEVENT", "UID:x\nDTSTART:20261112T090000Z\nDTEND:20261112T100000Z\n" + Alarm(":-PT15M"));
        _store.CreateItem(new Item("quiet1", "quiet", ItemKind.Event, null, ev));
        _store.CreateItem(new Item("shown", "cal", ItemKind.Event, null, ev));
        _store.CreateItem(new Item("t3", "todo", ItemKind.Task, null, Ics("VTODO",
            "UID:t3\nDUE:20261112T170000Z\nSUMMARY:Report\n" + Alarm(";RELATED=END:-PT1H")),
            Summary: "Report", DueUtc: At(12, 17)));
        Assert.Equal(["shown", "t3"], Collect().Select(r => r.ItemId));

        // Hidden in the sidebar or by a calendar set: still synced, so still reminded (calendar-sets R7).
        _store.SetCollectionHidden("cal", true);
        _store.SetCollectionHidden("todo", true);
        Assert.Equal(["shown", "t3"], Collect().Select(r => r.ItemId));
    }

    // ---- State (R2, R4) ----

    [Fact]
    public void State_RemembersHandledKeys_AndPrunesOldOnes()
    {
        var old = R("old", Now.AddDays(-10), Now.AddDays(-9));
        var recent = R("recent", Now.AddDays(-10), Now.AddDays(1));
        _store.MarkRemindersScheduled([old, recent, recent]);

        Assert.Equal(new HashSet<string> { old.Key, recent.Key }, _store.GetReminderStates().Keys.ToHashSet());

        // The occurrence still ahead keeps its row, so its late reminder never shows twice.
        _store.PruneReminderState(Now.AddDays(-7));
        Assert.Equal(new HashSet<string> { recent.Key }, _store.GetReminderStates().Keys.ToHashSet());
    }

    [Fact]
    public void State_ActionsReplaceScheduled_SchedulingNeverUndoesAnAction()
    {
        var a = R("a", Now.AddMinutes(-5), Now.AddHours(1));
        var b = R("b", Now.AddMinutes(-5), Now.AddHours(1));
        _store.MarkRemindersScheduled([a, b]);

        _store.SetReminderState([a], ReminderStates.Snoozed, Now.AddMinutes(10));
        _store.SetReminderState([b], ReminderStates.Dismissed);
        _store.MarkRemindersScheduled([a, b]);

        var states = _store.GetReminderStates();
        Assert.Equal(new ReminderStatus(ReminderStates.Snoozed, Now.AddMinutes(10)), states[a.Key]);
        Assert.Equal(new ReminderStatus(ReminderStates.Dismissed, null), states[b.Key]);

        // Dismissing a snoozed reminder clears its snooze time.
        _store.SetReminderState([a], ReminderStates.Dismissed);
        Assert.Equal(new ReminderStatus(ReminderStates.Dismissed, null), _store.GetReminderStates()[a.Key]);
    }

    [Fact]
    public void AwakeUntil_RoundTrips()
    {
        Assert.Null(_store.GetRemindersAwakeUntil());
        _store.SetRemindersAwakeUntil(Now);
        Assert.Equal(Now, _store.GetRemindersAwakeUntil());
    }
}
