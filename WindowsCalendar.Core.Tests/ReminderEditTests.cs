using System.Text.Json;
using System.Text.Json.Nodes;
using Ical.Net;
using Microsoft.Extensions.Time.Testing;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Reminders;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>Reminder editing and per-calendar settings (reminders-notifications R5, R5a).</summary>
public sealed class ReminderEditTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly Collection Home = new("c1", "a1", CollectionKind.Calendar, "Home", "Navy", "Circle", true, false, "https://dav.example.test/home/");
    private static readonly Collection Work = new("w1", "a2", CollectionKind.Calendar, "Calendar", "Navy", "Circle", true, false, "AAMk");

    private static Item CalDav(string alarms, string extra = "") =>
        CalDavItemMapper.Map("https://dav.example.test/home/x.ics", "\"e1\"",
            ("BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//test//EN\nBEGIN:VEVENT\nUID:x1\nDTSTAMP:20260101T000000Z\n"
             + "DTSTART:20261112T090000Z\nDTEND:20261112T100000Z\nSUMMARY:Review\n" + extra + alarms + "END:VEVENT\nEND:VCALENDAR\n").Replace("\n", "\r\n"),
            Utc)! with { Id = "i1", CollectionId = "c1" };

    private static string Alarm(string action, string trigger) => $"BEGIN:VALARM\nACTION:{action}\nDESCRIPTION:x\nTRIGGER{trigger}\nEND:VALARM\n";

    private static List<string> Triggers(Item item) =>
        Calendar.Load(item.RawPayload)!.Events[0].Alarms.Select(a => $"{a.Action} {a.Trigger?.Duration}").ToList();

    private static Item Graph(string json) => GraphEventMapper.Map(JsonDocument.Parse(json).RootElement, "w1", Utc)!;

    private static JsonObject Patch(Item item) => JsonNode.Parse(GraphEvents.PatchBody(item.RawPayload))!.AsObject();

    // ---- Labels and lists ----

    [Fact]
    public void Labels_TimedAndAllDay()
    {
        Assert.Equal(["None", "At start", "5 minutes before", "10 minutes before", "15 minutes before", "30 minutes before",
            "1 hour before", "2 hours before", "1 day before", "2 days before"],
            ReminderChoices.Timed.Select(c => ReminderChoices.Label(c, allDay: false)));
        Assert.Equal(["None", "On the day, 09:00", "1 day before, 09:00"],
            ReminderChoices.AllDay(graph: false).Select(c => ReminderChoices.Label(c, allDay: true)));
        Assert.Equal(["None", "1 day before, 09:00"], ReminderChoices.AllDay(graph: true).Select(c => ReminderChoices.Label(c, allDay: true)));
        Assert.Equal("45 minutes before", ReminderChoices.Label(ReminderChoice.Before(45), allDay: false));
        Assert.Equal("10 minutes after start", ReminderChoices.Label(ReminderChoice.Before(-10), allDay: false));
    }

    // ---- CalDAV: the first DISPLAY alarm; the others are kept ----

    [Fact]
    public void CalDav_ReadsFirstDisplayAlarm_AndCountsTheOthers()
    {
        var fields = CalDavEvents.Read(CalDav(Alarm("AUDIO", ":-PT1M") + Alarm("DISPLAY", ":-PT30M") + Alarm("DISPLAY", ":-P1D")), Utc);

        Assert.Equal(ReminderChoice.Before(30), fields.Reminder);
        Assert.Equal(2, fields.MoreReminders);
        Assert.Equal(ReminderChoice.None, CalDavEvents.Read(CalDav(""), Utc).Reminder);
    }

    [Fact]
    public void CalDav_FixedTimeOrEndRelative_IsKeptAndNotRewritten()
    {
        var item = CalDav(Alarm("DISPLAY", ";VALUE=DATE-TIME:20261112T070000Z"));
        var fields = CalDavEvents.Read(item, Utc);

        Assert.Equal("At Thu 12 Nov 07:00", fields.Reminder!.Kept);
        Assert.Null(CalDavEvents.Edit(item, fields, Utc, Now));
        Assert.Equal("10 min before end", CalDavEvents.Read(CalDav(Alarm("DISPLAY", ";RELATED=END:-PT10M")), Utc).Reminder!.Kept);
    }

    [Fact]
    public void CalDav_Edit_ChangesFirstAlarm_KeepsTheSecond_AddsAndRemoves()
    {
        var item = CalDav(Alarm("DISPLAY", ":-PT15M") + Alarm("DISPLAY", ":-P1D"));
        var fields = CalDavEvents.Read(item, Utc);

        var changed = CalDavEvents.Edit(item, fields with { Reminder = ReminderChoice.Before(60) }, Utc, Now)!;
        Assert.Equal(["DISPLAY -PT1H", "DISPLAY -P1D"], Triggers(changed));

        var removed = CalDavEvents.Edit(changed, fields with { Reminder = ReminderChoice.None }, Utc, Now)!;
        Assert.Equal(["DISPLAY -P1D"], Triggers(removed));

        var none = CalDav("");
        var added = CalDavEvents.Edit(none, CalDavEvents.Read(none, Utc) with { Reminder = ReminderChoice.Before(10) }, Utc, Now)!;
        Assert.Equal(["DISPLAY -PT10M"], Triggers(added));

        // Fields from elsewhere carry no reminder: the alarms are left alone.
        Assert.Null(CalDavEvents.Edit(item, fields with { Reminder = null }, Utc, Now));
    }

    [Fact]
    public void CalDav_Create_WithAReminder_FiresFromTheStore()
    {
        var fields = new EventFields("Standup", false, new DateTime(2026, 11, 12, 9, 0, 0), new DateTime(2026, 11, 12, 9, 15, 0), null, null,
            Reminder: ReminderChoice.Before(15));
        var created = CalDavEvents.Create(Home, fields, Utc, Now);

        Assert.Equal(["DISPLAY -PT15M"], Triggers(created));
        Assert.Empty(Triggers(CalDavEvents.Create(Home, fields with { Reminder = null }, Utc, Now)));
    }

    [Fact]
    public void CalDav_OneOccurrence_GetsItsOwnAlarm()
    {
        var item = CalDav(Alarm("DISPLAY", ":-PT15M"), "RRULE:FREQ=DAILY;COUNT=3\n") with { IsRecurring = true };
        var second = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        var fields = CalDavRecurrence.ReadOccurrence(item, second, Utc);

        var edited = CalDavRecurrence.EditOccurrence(item, second, fields with { Reminder = ReminderChoice.Before(5) }, Utc, Now)!;

        var events = Calendar.Load(edited.RawPayload)!.Events;
        Assert.Equal("-PT15M", events.Single(e => e.RecurrenceIdentifier is null).Alarms.Single().Trigger!.Duration.ToString());
        Assert.Equal("-PT5M", events.Single(e => e.RecurrenceIdentifier is not null).Alarms.Single().Trigger!.Duration.ToString());
    }

    // ---- MS365 events: isReminderOn and reminderMinutesBeforeStart ----

    [Fact]
    public void Graph_ReadsAndPatchesOnlyTheReminderFields()
    {
        var item = Graph("""
            {"id":"e1","subject":"Call","isReminderOn":true,"reminderMinutesBeforeStart":15,
             "start":{"dateTime":"2026-11-12T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-12T10:00:00","timeZone":"UTC"}}
            """);
        var fields = GraphEventMapper.Read(item, null, Utc);
        Assert.Equal(ReminderChoice.Before(15), fields.Reminder);

        var later = GraphEvents.Edit(item, null, fields with { Reminder = ReminderChoice.Before(30) }, Utc)!;
        Assert.Equal("""{"isReminderOn":true,"reminderMinutesBeforeStart":30}""", Patch(later).ToJsonString());

        var off = GraphEvents.Edit(item, null, fields with { Reminder = ReminderChoice.None }, Utc)!;
        Assert.Equal("""{"isReminderOn":false}""", Patch(off).ToJsonString());
        Assert.Equal(ReminderChoice.None, GraphEventMapper.Read(off, null, Utc).Reminder);
    }

    [Fact]
    public void Graph_Meeting_ReminderAloneIsEditable_OccurrenceFallsBackToSeries()
    {
        var master = Graph("""
            {"id":"m1","type":"seriesMaster","subject":"Weekly","isReminderOn":true,"reminderMinutesBeforeStart":10,
             "attendees":[{"emailAddress":{"address":"a@example.test"}}],"isOrganizer":false,
             "start":{"dateTime":"2026-11-12T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-12T10:00:00","timeZone":"UTC"}}
            """);
        var stub = Graph("""
            {"id":"o1","type":"occurrence","seriesMasterId":"m1",
             "start":{"dateTime":"2026-11-19T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-19T10:00:00","timeZone":"UTC"}}
            """);
        Assert.Equal(ReminderChoice.Before(10), GraphEventMapper.Read(stub, master, Utc).Reminder);

        Assert.Null(GraphEvents.EditReminder(stub, master, ReminderChoice.Before(10), Utc));
        var edited = GraphEvents.EditReminder(stub, master, ReminderChoice.Before(5), Utc)!;
        Assert.Equal("""{"isReminderOn":true,"reminderMinutesBeforeStart":5}""", Patch(edited).ToJsonString());
    }

    [Fact]
    public void Graph_Create_WritesTheReminder()
    {
        var fields = new EventFields("New", false, new DateTime(2026, 11, 12, 9, 0, 0), new DateTime(2026, 11, 12, 10, 0, 0), null, null,
            Reminder: ReminderChoice.Before(15));
        var body = JsonNode.Parse(GraphEvents.CreateBody(GraphEvents.Create(Work, fields, Utc).RawPayload))!;

        Assert.True(body["isReminderOn"]!.GetValue<bool>());
        Assert.Equal(15, body["reminderMinutesBeforeStart"]!.GetValue<int>());
    }

    // ---- To Do tasks: isReminderOn and reminderDateTime ----

    [Fact]
    public void Task_ReminderTime_ReadSetAndCleared()
    {
        var task = GraphTaskMapper.Map(JsonDocument.Parse("""
            {"id":"t1","title":"Pay","status":"notStarted","importance":"normal","isReminderOn":true,
             "reminderDateTime":{"dateTime":"2026-11-12T09:30:00.0000000","timeZone":"UTC"}}
            """).RootElement, "todo", Utc)!;
        var fields = GraphTaskMapper.Read(task, Utc);
        Assert.Equal(new DateTime(2026, 11, 12, 9, 30, 0), fields.Reminder);

        var moved = GraphTasks.Edit(task, fields with { Reminder = new DateTime(2026, 11, 13, 8, 0, 0) }, Utc)!;
        Assert.Equal("""{"isReminderOn":true,"reminderDateTime":{"dateTime":"2026-11-13T08:00:00","timeZone":"UTC"}}""", Patch(moved).ToJsonString());

        var cleared = GraphTasks.Edit(task, fields with { Reminder = null }, Utc)!;
        Assert.Null(GraphTaskMapper.Read(cleared, Utc).Reminder);
        Assert.Null(GraphTasks.Edit(task, fields, Utc));
    }

    // ---- Per-calendar settings (R5) ----

    [Fact]
    public void Settings_DefaultReminder_RemindersOn_HolidayStartsOff()
    {
        var dir = Directory.CreateTempSubdirectory("wincal-remset-").FullName;
        try
        {
            using (var store = LocalStore.Open(Path.Combine(dir, "store.db"), new FakeTimeProvider(Now)))
            {
                store.AddAccount(new Account("a1", AccountType.CalDAV, "Radicale"));
                store.AddCollection(Home);
                store.AddCollection(Home with { Id = "hol", Name = "Holidays" });

                Assert.Equal(15, store.GetDefaultReminder("c1"));
                store.SetDefaultReminder("c1", null);
                Assert.Null(store.GetDefaultReminder("c1"));
                store.SetDefaultReminder("c1", 60);
                Assert.Equal(60, store.GetDefaultReminder("c1"));

                store.SetCollectionRemindersOn("c1", false);
                store.SetHolidayCollection("hol");
                Assert.All(store.GetCollections("a1"), c => Assert.False(c.RemindersOn));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
