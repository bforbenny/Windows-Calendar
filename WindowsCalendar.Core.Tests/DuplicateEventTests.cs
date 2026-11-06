using System.Text.Json;
using System.Text.Json.Nodes;
using Ical.Net;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>Copies of events (duplicate-event R3-R5).</summary>
public sealed class DuplicateEventTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 1, 0, 0, TimeSpan.Zero);
    private static readonly Collection Home = new("c1", "a1", CollectionKind.Calendar, "Home", "blue", "circle", true, false,
        "https://dav.example.test/ben/home/");
    private static readonly Collection Work = new("c2", "a1", CollectionKind.Calendar, "Work", "red", "circle", true, false,
        "https://dav.example.test/ben/work/");

    private static Item CalDav(string body) =>
        CalDavItemMapper.Map("https://dav.example.test/ben/home/x.ics", "\"e1\"",
            ("BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Test//EN\n" + body + "END:VCALENDAR\n").Replace("\n", "\r\n"), London)!
        with { Id = "i1", CollectionId = "c1" };

    // A meeting with a reminder, a located place and a conference link, 09:00-10:00 London.
    private const string Meeting = """
        BEGIN:VEVENT
        UID:m1
        DTSTAMP:20260101T000000Z
        DTSTART;TZID=Europe/London:20261110T090000
        DTEND;TZID=Europe/London:20261110T100000
        SUMMARY:Planning
        DESCRIPTION:Agenda
        LOCATION:Room 1
        GEO:51.5;-0.12
        TRANSP:TRANSPARENT
        SEQUENCE:4
        ORGANIZER:mailto:ben@example.test
        ATTENDEE;PARTSTAT=ACCEPTED:mailto:ann@example.test
        CONFERENCE;VALUE=URI:https://meet.example.test/abc
        BEGIN:VALARM
        ACTION:DISPLAY
        TRIGGER:-PT15M
        DESCRIPTION:Reminder
        END:VALARM
        END:VEVENT

        """;

    // Mondays 09:00-10:00 London; the 9 Nov occurrence is moved to 11:00 and renamed.
    private const string Weekly = """
        BEGIN:VEVENT
        UID:w1
        DTSTAMP:20260101T000000Z
        DTSTART;TZID=Europe/London:20261102T090000
        DTEND;TZID=Europe/London:20261102T100000
        RRULE:FREQ=WEEKLY;BYDAY=MO
        EXDATE;TZID=Europe/London:20261123T090000
        SUMMARY:Standup
        END:VEVENT
        BEGIN:VEVENT
        UID:w1
        DTSTAMP:20260101T000000Z
        RECURRENCE-ID;TZID=Europe/London:20261109T090000
        DTSTART;TZID=Europe/London:20261109T110000
        DTEND;TZID=Europe/London:20261109T120000
        SUMMARY:Standup (moved)
        END:VEVENT

        """;

    [Fact]
    public void CalDav_CopiesFields_WithNewUid_AndNoAttendees()
    {
        var item = CalDav(Meeting);

        var copy = CalDavEvents.Duplicate(item, null, Home, London, Now);

        var ev = Calendar.Load(copy.RawPayload)!.Events.Single();
        Assert.NotEqual("m1", ev.Uid);
        Assert.NotEqual(item.Id, copy.Id);
        Assert.Equal(CalDavEvents.Href(Home, ev.Uid!), copy.RemoteId);
        Assert.Null(copy.Etag);
        Assert.Equal(0, ev.Sequence);
        Assert.Equal(CalDavEvents.Read(item, London), CalDavEvents.Read(copy, London));
        Assert.Equal("Europe/London", ev.DtStart!.TzId);
        Assert.Contains("GEO:", copy.RawPayload);
        Assert.Contains("TRANSP:TRANSPARENT", copy.RawPayload);
        Assert.Single(ev.Alarms);
        Assert.DoesNotContain("ATTENDEE", copy.RawPayload);
        Assert.DoesNotContain("ORGANIZER", copy.RawPayload);
        Assert.DoesNotContain("CONFERENCE", copy.RawPayload);
    }

    [Fact]
    public void CalDav_IntoAnotherCalendar_GetsThatCalendarsHref()
    {
        var copy = CalDavEvents.Duplicate(CalDav(Meeting), null, Work, London, Now);

        Assert.Equal("c2", copy.CollectionId);
        Assert.StartsWith("https://dav.example.test/ben/work/", copy.RemoteId);
    }

    [Fact]
    public void CalDav_AtADrop_MovesTheCopyOnly()
    {
        var item = CalDav(Meeting);

        var copy = CalDavEvents.Duplicate(item, null, Home, London, Now, (false, new DateTime(2026, 11, 12, 14, 0, 0), new DateTime(2026, 11, 12, 15, 0, 0)));

        var f = CalDavEvents.Read(copy, London);
        Assert.Equal(new DateTime(2026, 11, 12, 14, 0, 0), f.Start);
        Assert.Equal(new DateTime(2026, 11, 12, 15, 0, 0), f.End);
        Assert.Equal("Planning", f.Title);
        Assert.Equal("Europe/London", Calendar.Load(copy.RawPayload)!.Events.Single().DtStart!.TzId);
    }

    [Fact]
    public void CalDav_Occurrence_BecomesASingleEventAtThatOccurrence()
    {
        var item = CalDav(Weekly);
        var nov16 = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

        var copy = CalDavEvents.Duplicate(item, nov16, Home, London, Now);

        var ev = Calendar.Load(copy.RawPayload)!.Events.Single();
        Assert.Null(ev.RecurrenceRule);
        Assert.DoesNotContain("EXDATE", copy.RawPayload);
        Assert.Null(ev.RecurrenceIdentifier);
        Assert.False(copy.IsRecurring);
        Assert.Equal(new DateTime(2026, 11, 16, 9, 0, 0), CalDavEvents.Read(copy, London).Start);
    }

    [Fact]
    public void CalDav_OverriddenOccurrence_CopiesTheOverride()
    {
        var copy = CalDavEvents.Duplicate(CalDav(Weekly), new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero), Home, London, Now);

        var f = CalDavEvents.Read(copy, London);
        Assert.Equal("Standup (moved)", f.Title);
        Assert.Equal(new DateTime(2026, 11, 9, 11, 0, 0), f.Start);
        Assert.Null(Calendar.Load(copy.RawPayload)!.Events.Single().RecurrenceIdentifier);
    }

    [Fact]
    public void CalDav_Series_CopiesRuleAndOverrides_UnderOneNewUid()
    {
        var copy = CalDavEvents.Duplicate(CalDav(Weekly), null, Home, London, Now);

        var events = Calendar.Load(copy.RawPayload)!.Events;
        Assert.Equal(2, events.Count);
        Assert.Single(events.Select(e => e.Uid).Distinct());
        Assert.NotEqual("w1", events[0].Uid);
        Assert.True(copy.IsRecurring);
    }

    // ---- MS365 ----

    private static readonly Collection Ms365 = new("g1", "a2", CollectionKind.Calendar, "Calendar", "blue", "circle", true, false, "cal1");

    private static Item Graph(string json) => GraphEventMapper.Map(JsonDocument.Parse(json).RootElement, "g1", Utc)!;

    private static JsonObject Json(Item item) => JsonNode.Parse(item.RawPayload)!.AsObject();

    private const string TeamsMeeting = """
        {"id":"e1","@odata.etag":"W/\"1\"","type":"singleInstance","subject":"Review","isOrganizer":true,
         "body":{"contentType":"text","content":"Notes"},"showAs":"tentative","isReminderOn":true,"reminderMinutesBeforeStart":30,
         "location":{"displayName":"Room 1","coordinates":{"latitude":51.5,"longitude":-0.12}},
         "attendees":[{"emailAddress":{"address":"ann@example.test"},"type":"required"}],
         "organizer":{"emailAddress":{"address":"ben@example.test"}},
         "isOnlineMeeting":true,"onlineMeetingProvider":"teamsForBusiness","onlineMeeting":{"joinUrl":"https://teams.example.test/j"},
         "originalStartTimeZone":"GMT Standard Time",
         "start":{"dateTime":"2026-11-10T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-10T10:00:00","timeZone":"UTC"} }
        """;

    [Fact]
    public void Graph_CopiesWritableFields_WithoutAttendeesOrMeeting()
    {
        var item = Graph(TeamsMeeting);

        var copy = GraphEvents.Duplicate(item, null, Ms365, Utc);

        var json = Json(copy);
        Assert.Null(copy.RemoteId);
        Assert.NotEqual(item.Id, copy.Id);
        Assert.False(copy.ReadOnly);
        Assert.Equal("Review", json["subject"]!.GetValue<string>());
        Assert.Equal("tentative", json["showAs"]!.GetValue<string>());
        Assert.Equal(30, json["reminderMinutesBeforeStart"]!.GetValue<int>());
        Assert.Equal(51.5, json["location"]!["coordinates"]!["latitude"]!.GetValue<double>());
        foreach (var name in new[] { "id", "attendees", "organizer", "isOnlineMeeting", "onlineMeeting", "onlineMeetingProvider", "@odata.etag" })
        {
            Assert.False(json.ContainsKey(name), name);
        }
        // Written in the event's own timezone (GMT in November, so the same wall time).
        Assert.Equal("GMT Standard Time", json["start"]!["timeZone"]!.GetValue<string>());
        Assert.Equal(item.StartUtc, copy.StartUtc);
        Assert.Equal(item.EndUtc, copy.EndUtc);
    }

    [Fact]
    public void Graph_AtADrop_MovesTheCopy()
    {
        var copy = GraphEvents.Duplicate(Graph(TeamsMeeting), null, Ms365, Utc, (true, new DateTime(2026, 11, 12), new DateTime(2026, 11, 13)));

        Assert.True(copy.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero), copy.StartUtc);
    }

    [Fact]
    public void Graph_StubOccurrence_BecomesASingleEventWithItsMastersFields()
    {
        var master = Graph("""
            {"id":"m1","type":"seriesMaster","subject":"Sync","body":{"contentType":"text","content":"Notes"},
             "recurrence":{"pattern":{"type":"weekly","interval":1,"daysOfWeek":["tuesday"]},"range":{"type":"noEnd","startDate":"2026-11-10"}},
             "start":{"dateTime":"2026-11-10T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-10T10:00:00","timeZone":"UTC"} }
            """);
        var stub = Graph("""
            {"id":"o1","type":"occurrence","seriesMasterId":"m1",
             "start":{"dateTime":"2026-11-17T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-17T10:00:00","timeZone":"UTC"}}
            """);

        var copy = GraphEvents.Duplicate(stub, master, Ms365, Utc);

        var json = Json(copy);
        Assert.Equal("Sync", json["subject"]!.GetValue<string>());
        Assert.False(json.ContainsKey("recurrence"));
        Assert.False(copy.IsRecurring);
        Assert.Null(copy.SeriesId);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero), copy.StartUtc);
    }
}
