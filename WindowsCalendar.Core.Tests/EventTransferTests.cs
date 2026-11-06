using System.Text.Json;
using System.Text.Json.Nodes;
using Ical.Net;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Sync;

namespace WindowsCalendar.Core.Tests;

/// <summary>Moving events to a calendar in another account (calendar-views R8).</summary>
public sealed class EventTransferTests
{
    private static readonly TimeZoneInfo Perth = TimeZoneInfo.FindSystemTimeZoneById("Australia/Perth");
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 1, 0, 0, TimeSpan.Zero);
    private static readonly Collection Radicale = new("r1", "a1", CollectionKind.Calendar, "Home", "blue", "circle", true, false,
        "https://dav.example.test/ben/home/");
    private static readonly Collection Radicale2 = new("r2", "a3", CollectionKind.Calendar, "Club", "green", "circle", true, false,
        "https://dav.other.test/ben/club/");
    private static readonly Collection Ms365 = new("g1", "a2", CollectionKind.Calendar, "Calendar", "red", "circle", true, false, "cal1");

    private static Item CalDav(string body) =>
        CalDavItemMapper.Map("https://dav.example.test/ben/home/x.ics", "\"e1\"",
            ("BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Test//EN\n" + body + "END:VCALENDAR\n").Replace("\n", "\r\n"), Perth)!
        with { Id = "i1", CollectionId = "r1" };

    private static Item Graph(string json) => GraphEventMapper.Map(JsonDocument.Parse(json).RootElement, "g1", Perth)! with { Id = "i2" };

    private static JsonObject Json(Item item) => JsonNode.Parse(item.RawPayload)!.AsObject();

    // Weekly on Tuesdays, 09:00-10:00 London, with everything that maps.
    private const string Rich = """
        BEGIN:VEVENT
        UID:x1
        DTSTAMP:20260101T000000Z
        DTSTART;TZID=Europe/London:20261110T090000
        DTEND;TZID=Europe/London:20261110T100000
        RRULE:FREQ=WEEKLY;BYDAY=TU;COUNT=5
        EXDATE;TZID=Europe/London:20261117T090000
        SUMMARY:Choir
        DESCRIPTION:Bring music
        LOCATION:Hall
        GEO:51.5;-0.12
        X-MICROSOFT-CDO-BUSYSTATUS:TENTATIVE
        CLASS:PRIVATE
        PRIORITY:1
        CATEGORIES:Music,Weekly
        BEGIN:VALARM
        ACTION:DISPLAY
        TRIGGER:-PT30M
        DESCRIPTION:Reminder
        END:VALARM
        BEGIN:VALARM
        ACTION:DISPLAY
        TRIGGER:-P1D
        DESCRIPTION:Reminder
        END:VALARM
        END:VEVENT

        """;

    [Fact]
    public void CalDavToGraph_MapsEveryField()
    {
        var result = EventTransfer.Move(CalDav(Rich), false, Ms365, true, Perth, Now);

        var json = Json(result.Moved);
        Assert.Equal("g1", result.Moved.CollectionId);
        Assert.Null(result.Moved.RemoteId);
        Assert.Equal("Choir", json["subject"]!.GetValue<string>());
        Assert.Equal("Bring music", json["body"]!["content"]!.GetValue<string>());
        Assert.Equal("Hall", json["location"]!["displayName"]!.GetValue<string>());
        Assert.Equal(51.5, json["location"]!["coordinates"]!["latitude"]!.GetValue<double>());
        Assert.Equal("GMT Standard Time", json["start"]!["timeZone"]!.GetValue<string>());
        Assert.Equal("2026-11-10T09:00:00", json["start"]!["dateTime"]!.GetValue<string>());
        Assert.True(json["isReminderOn"]!.GetValue<bool>());
        Assert.Equal(30, json["reminderMinutesBeforeStart"]!.GetValue<int>());
        Assert.Equal("tentative", json["showAs"]!.GetValue<string>());
        Assert.Equal("private", json["sensitivity"]!.GetValue<string>());
        Assert.Equal("high", json["importance"]!.GetValue<string>());
        Assert.Equal(["Music", "Weekly"], json["categories"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("weekly", json["recurrence"]!["pattern"]!["type"]!.GetValue<string>());
        Assert.Equal(5, json["recurrence"]!["range"]!["numberOfOccurrences"]!.GetValue<int>());
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero), result.Moved.StartUtc);
        Assert.Equal(["1 other reminder", "1 deleted occurrence (it comes back)"], result.Lost);
    }

    [Fact]
    public void CalDavToGraph_TransparentWithoutBusyStatus_IsFree_AndNoAlarmIsNoReminder()
    {
        var item = CalDav("""
            BEGIN:VEVENT
            UID:x2
            DTSTAMP:20260101T000000Z
            DTSTART;VALUE=DATE:20261110
            DTEND;VALUE=DATE:20261111
            SUMMARY:Holiday
            TRANSP:TRANSPARENT
            END:VEVENT

            """);

        var result = EventTransfer.Move(item, false, Ms365, true, Perth, Now);

        var json = Json(result.Moved);
        Assert.Equal("free", json["showAs"]!.GetValue<string>());
        Assert.False(json["isReminderOn"]!.GetValue<bool>());
        Assert.True(result.Moved.AllDay);
        Assert.Empty(result.Lost);
    }

    [Fact]
    public void CalDavToRadicale_CopiesTheWholeEvent()
    {
        var item = CalDav(Rich);

        var result = EventTransfer.Move(item, false, Radicale2, false, Perth, Now);

        Assert.StartsWith("https://dav.other.test/ben/club/", result.Moved.RemoteId);
        Assert.Equal(item.RawPayload, result.Moved.RawPayload);
        Assert.Empty(result.Lost);
    }

    private const string GraphRich = """
        {"id":"e1","@odata.etag":"W/\"1\"","type":"singleInstance","subject":"Dentist","isOrganizer":true,
         "body":{"contentType":"html","content":"<html><head><style>p{}</style></head><body><p>Line&nbsp;one</p><p>Line 2</p></body></html>"},
         "showAs":"oof","isReminderOn":true,"reminderMinutesBeforeStart":45,"sensitivity":"confidential","importance":"low",
         "categories":["Health"],
         "location":{"displayName":"Clinic","coordinates":{"latitude":-31.95,"longitude":115.86}},
         "originalStartTimeZone":"GMT Standard Time",
         "start":{"dateTime":"2026-11-10T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-10T10:00:00","timeZone":"UTC"} }
        """;

    [Fact]
    public void GraphToCalDav_MapsEveryField()
    {
        var result = EventTransfer.Move(Graph(GraphRich), true, Radicale, false, Perth, Now);

        var ev = Calendar.Load(result.Moved.RawPayload)!.Events.Single();
        Assert.StartsWith("https://dav.example.test/ben/home/", result.Moved.RemoteId);
        Assert.Equal("Dentist", ev.Summary);
        Assert.Equal("Line one\nLine 2", ev.Description);
        Assert.Equal("Clinic", ev.Location);
        Assert.Equal(-31.95, ev.GeographicLocation!.Latitude);
        Assert.Equal("Europe/London", ev.DtStart!.TzId);
        Assert.Equal(new DateTime(2026, 11, 10, 9, 0, 0), ev.DtStart.Value);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero), result.Moved.StartUtc);
        Assert.Contains("TRIGGER:-PT45M", result.Moved.RawPayload);
        Assert.Equal("OPAQUE", ev.Transparency);
        Assert.Contains("X-MICROSOFT-CDO-BUSYSTATUS:OOF", result.Moved.RawPayload);
        Assert.Equal("CONFIDENTIAL", ev.Class);
        Assert.Equal(9, ev.Priority);
        Assert.Equal(["Health"], ev.Categories);
        Assert.Equal(["the description's formatting"], result.Lost);
    }

    [Fact]
    public void GraphToGraph_CopiesTheWritableFields()
    {
        var result = EventTransfer.Move(Graph(GraphRich), true, Ms365 with { Id = "g2", AccountId = "a4" }, true, Perth, Now);

        Assert.Equal("g2", result.Moved.CollectionId);
        Assert.Equal("oof", Json(result.Moved)["showAs"]!.GetValue<string>());
        Assert.Empty(result.Lost);
    }

    [Theory]
    [InlineData("ATTENDEE:mailto:ann@example.test")]
    [InlineData("ORGANIZER:mailto:ann@example.test")]
    public void CalDav_MeetingsAndInvitations_AreRefused(string line)
    {
        var item = CalDav(Rich.Replace("SUMMARY:Choir", "SUMMARY:Choir\n" + line));

        Assert.True(EventTransfer.IsMeeting(item));
        Assert.Throws<EditRefusedException>(() => EventTransfer.Move(item, false, Ms365, true, Perth, Now));
        Assert.Throws<EditRefusedException>(() => EventTransfer.Move(item, false, Radicale2, false, Perth, Now));
    }

    [Fact]
    public void Graph_MeetingsAndRepeatingEvents_AreRefused()
    {
        var meeting = Graph(GraphRich.Replace("\"isOrganizer\":true", "\"isOrganizer\":false"));
        var series = Graph(GraphRich.Replace("\"type\":\"singleInstance\"", "\"type\":\"seriesMaster\",\"recurrence\":{\"pattern\":{\"type\":\"daily\",\"interval\":1},\"range\":{\"type\":\"noEnd\",\"startDate\":\"2026-11-10\"}}"));

        Assert.Throws<EditRefusedException>(() => EventTransfer.Move(meeting, true, Radicale, false, Perth, Now));
        Assert.Throws<EditRefusedException>(() => EventTransfer.Move(series, true, Radicale, false, Perth, Now));
    }

    [Fact]
    public void CalDav_UnsupportedRule_IsRefusedForMs365()
    {
        var item = CalDav(Rich.Replace("RRULE:FREQ=WEEKLY;BYDAY=TU;COUNT=5", "RRULE:FREQ=WEEKLY;BYDAY=TU;BYHOUR=9,15"));

        Assert.Throws<EditRefusedException>(() => EventTransfer.Move(item, false, Ms365, true, Perth, Now));
    }
}
