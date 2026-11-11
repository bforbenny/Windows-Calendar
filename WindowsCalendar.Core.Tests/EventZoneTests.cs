using System.Text.Json;
using System.Text.Json.Nodes;
using Ical.Net;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Sync;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Core.Tests;

/// <summary>Per-event timezones for single events and whole series, from a London system zone (timezone-handling R7).</summary>
public sealed class EventZoneTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("AUS Eastern Standard Time");
    private static readonly TimeZoneInfo GmtWindows = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 1, 0, 0, TimeSpan.Zero);
    private static readonly Collection Radicale = new("r1", "a1", CollectionKind.Calendar, "Home", "blue", "circle", true, false,
        "https://dav.example.test/ben/home/");
    private static readonly Collection Ms365 = new("g1", "a2", CollectionKind.Calendar, "Calendar", "red", "circle", true, false, "cal1");

    private static EventFields Fields(DateTime start, DateTime end, TimeZoneInfo? zone = null, TimeZoneInfo? endZone = null) =>
        new("Call", false, start, end, null, null, StartZone: zone, EndZone: endZone);

    private static Item CalDav(string body) =>
        CalDavItemMapper.Map("https://dav.example.test/ben/home/x.ics", "\"e1\"",
            ("BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Test//EN\n" + body + "END:VCALENDAR\n").Replace("\n", "\r\n"), London)!
        with { Id = "i1", CollectionId = "r1" };

    private static Item Graph(string json) => GraphEventMapper.Map(JsonDocument.Parse(json).RootElement, "g1", London)! with { Id = "i2" };

    private static JsonObject Json(Item item) => JsonNode.Parse(item.RawPayload)!.AsObject();

    private static (string DateTime, string TimeZone) Graph(JsonNode? t) => (t!["dateTime"]!.GetValue<string>(), t["timeZone"]!.GetValue<string>());

    private static readonly DateTime Nov10At9 = new(2026, 11, 10, 9, 0, 0);

    [Fact]
    public void CalDavCreate_InNewYork_WritesItsTzidAndVTimezone()
    {
        var item = CalDavEvents.Create(Radicale, Fields(Nov10At9, Nov10At9.AddHours(1), NewYork), London, Now);

        Assert.Contains("DTSTART;TZID=America/New_York:20261110T090000", item.RawPayload);
        Assert.Contains("DTEND;TZID=America/New_York:20261110T100000", item.RawPayload);
        Assert.Single(Calendar.Load(item.RawPayload)!.TimeZones, z => z.TzId == "America/New_York");
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero), item.StartUtc);
    }

    [Fact]
    public void GraphCreate_InNewYork_WritesTheWindowsZone()
    {
        var item = GraphEvents.Create(Ms365, Fields(Nov10At9, Nov10At9.AddHours(1), NewYork), London);

        Assert.Equal(("2026-11-10T09:00:00", "Eastern Standard Time"), Graph(Json(item)["start"]));
        Assert.Equal(("2026-11-10T10:00:00", "Eastern Standard Time"), Graph(Json(item)["end"]));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero), item.StartUtc);
    }

    [Fact]
    public void Create_WithASeparateEndZone_SydneyToLondon()
    {
        // 09:00 in Sydney (AEDT, 22:00 UTC the day before) to 20:00 in London (GMT).
        var fields = Fields(new DateTime(2026, 11, 11, 9, 0, 0), new DateTime(2026, 11, 11, 20, 0, 0), Sydney, London);

        var caldav = CalDavEvents.Create(Radicale, fields, London, Now);
        var graph = GraphEvents.Create(Ms365, fields, London);

        Assert.Contains("DTSTART;TZID=Australia/Sydney:20261111T090000", caldav.RawPayload);
        Assert.Contains("DTEND;TZID=Europe/London:20261111T200000", caldav.RawPayload);
        Assert.Equal(("2026-11-11T09:00:00", "AUS Eastern Standard Time"), Graph(Json(graph)["start"]));
        Assert.Equal(("2026-11-11T20:00:00", "GMT Standard Time"), Graph(Json(graph)["end"]));
        Assert.Equal((new DateTimeOffset(2026, 11, 10, 22, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 11, 20, 0, 0, TimeSpan.Zero)),
            (caldav.StartUtc, caldav.EndUtc));
        Assert.Equal((caldav.StartUtc, caldav.EndUtc), (graph.StartUtc, graph.EndUtc));
    }

    [Fact]
    public void CalDavEdit_NewZone_KeepsTheWallClockInIt()
    {
        var item = CalDav("""
            BEGIN:VEVENT
            UID:x1
            DTSTAMP:20260101T000000Z
            DTSTART;TZID=Europe/London:20261110T090000
            DTEND;TZID=Europe/London:20261110T100000
            SUMMARY:Call
            END:VEVENT

            """);
        var f = CalDavEvents.Read(item, London);

        var edited = CalDavEvents.Edit(item, f with { StartZone = NewYork }, London, Now)!;

        Assert.Contains("DTSTART;TZID=America/New_York:20261110T090000", edited.RawPayload);
        Assert.Contains("DTEND;TZID=America/New_York:20261110T100000", edited.RawPayload);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero), edited.StartUtc);
        Assert.Equal((NewYork.Id, null, false), Ids(TimeZoneLabels.OwnZones(edited, London)));
    }

    [Fact]
    public void GraphEdit_NewZone_KeepsTheWallClockInIt_AndSendsOnlyTheTimes()
    {
        var item = Graph("""
            {"id":"e1","type":"singleInstance","subject":"Call","originalStartTimeZone":"GMT Standard Time","originalEndTimeZone":"GMT Standard Time",
             "start":{"dateTime":"2026-11-10T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-10T10:00:00","timeZone":"UTC"}}
            """);
        var f = GraphEventMapper.Read(item, null, London);

        var edited = GraphEvents.Edit(item, null, f with { StartZone = NewYork }, London)!;

        var patch = JsonNode.Parse(GraphEvents.PatchBody(edited.RawPayload))!.AsObject();
        Assert.Equal(["end", "isAllDay", "start"], patch.Select(p => p.Key).Order());
        Assert.Equal(("2026-11-10T09:00:00", "Eastern Standard Time"), Graph(patch["start"]));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero), edited.StartUtc);
        // Until the next sync the stored event is in its new zone, so a later time edit keeps it.
        Assert.Equal((NewYork.Id, null, false), Ids(TimeZoneLabels.OwnZones(edited, London)));
        var g = GraphEventMapper.Read(edited, null, London);
        var later = GraphEvents.Edit(edited, null, g with { Start = g.Start.AddHours(1), End = g.End.AddHours(1) }, London)!;
        Assert.Equal(("2026-11-10T10:00:00", "Eastern Standard Time"), Graph(Json(later)["start"]));
    }

    [Fact]
    public void Untouched_FloatingStaysFloating_AndCustomZonesAreKept()
    {
        var floating = CalDav("""
            BEGIN:VEVENT
            UID:f1
            DTSTAMP:20260101T000000Z
            DTSTART:20261110T090000
            DTEND:20261110T100000
            SUMMARY:Call
            END:VEVENT

            """);
        var custom = Graph("""
            {"id":"e1","type":"singleInstance","subject":"Call","originalStartTimeZone":"tzone://Microsoft/Custom",
             "start":{"dateTime":"2026-11-10T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-10T10:00:00","timeZone":"UTC"}}
            """);

        Assert.Equal((null, null, true), Ids(TimeZoneLabels.OwnZones(floating, London)));
        Assert.Equal((null, null, false), Ids(TimeZoneLabels.OwnZones(custom, London)));
        var f = CalDavEvents.Read(floating, London);
        var edited = CalDavEvents.Edit(floating, f with { Start = f.Start.AddHours(1), End = f.End.AddHours(1) }, London, Now)!;
        Assert.Contains("DTSTART:20261110T100000", edited.RawPayload);
        var g = GraphEventMapper.Read(custom, null, London);
        var graph = GraphEvents.Edit(custom, null, g with { Title = "Renamed" }, London)!;
        Assert.Equal("tzone://Microsoft/Custom", Json(graph)["originalStartTimeZone"]!.GetValue<string>());
        Assert.DoesNotContain("start", GraphEvents.PatchBody(graph.RawPayload));
    }

    [Fact]
    public void CalDavSeries_NewZone_KeepsTheSameOccurrences()
    {
        // Mondays 09:00 London to 14 Dec, 16 Nov deleted; 23 Nov moved unchanged, 30 Nov moved to 11:00.
        var item = CalDav("""
            BEGIN:VEVENT
            UID:w1
            DTSTAMP:20260101T000000Z
            DTSTART;TZID=Europe/London:20261102T090000
            DTEND;TZID=Europe/London:20261102T100000
            RRULE:FREQ=WEEKLY;BYDAY=MO;UNTIL=20261214T090000Z
            EXDATE;TZID=Europe/London:20261116T090000
            SUMMARY:Standup
            END:VEVENT
            BEGIN:VEVENT
            UID:w1
            DTSTAMP:20260101T000000Z
            RECURRENCE-ID;TZID=Europe/London:20261123T090000
            DTSTART;TZID=Europe/London:20261123T090000
            DTEND;TZID=Europe/London:20261123T100000
            SUMMARY:Standup (notes)
            END:VEVENT
            BEGIN:VEVENT
            UID:w1
            DTSTAMP:20260101T000000Z
            RECURRENCE-ID;TZID=Europe/London:20261130T090000
            DTSTART;TZID=Europe/London:20261130T110000
            DTEND;TZID=Europe/London:20261130T120000
            SUMMARY:Standup (late)
            END:VEVENT

            """);
        var nov9 = new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);
        var f = CalDavRecurrence.ReadOccurrence(item, nov9, London);
        var fields = f with { Start = new DateTime(2026, 11, 9, 9, 0, 0), End = new DateTime(2026, 11, 9, 10, 0, 0), StartZone = NewYork };

        var moved = CalDavRecurrence.MovedRecurrence(item, nov9, fields, London);
        var edited = CalDavRecurrence.EditSeries(item, nov9, fields, London, Now)!;

        var data = edited.RawPayload;
        Assert.Contains("DTSTART;TZID=America/New_York:20261102T090000", data);
        Assert.Contains("UNTIL=20261214T140000Z", data);
        Assert.Contains("EXDATE;TZID=America/New_York:20261116T090000", data);
        Assert.Contains("RECURRENCE-ID;TZID=America/New_York:20261123T090000", data);
        Assert.Contains("RECURRENCE-ID;TZID=America/New_York:20261130T090000", data);
        // The moved occurrence keeps its own time.
        Assert.Contains("DTSTART;TZID=Europe/London:20261130T110000", data);
        Assert.Equal(0, CalDavRecurrence.LostOverrides(item, edited));
        Assert.Equal(nov9.AddHours(5), moved);

        var occurrences = OccurrenceExpander.Expand(Calendar.Load(data)!, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), London).OrderBy(o => o.Start).ToList();
        Assert.Equal([2, 9, 23, 30, 7, 14], occurrences.Select(o => o.Recurrence.Day));
        Assert.All(occurrences.Where(o => o.Recurrence.Day != 30), o => Assert.Equal(14, o.Start.UtcDateTime.Hour));
        Assert.Equal(new DateTimeOffset(2026, 11, 30, 11, 0, 0, TimeSpan.Zero), occurrences.Single(o => o.Recurrence.Day == 30).Start);
    }

    [Fact]
    public void CalDavSeries_NewZoneAndTime_ReportsOverridesThatAreNoLongerOccurrences()
    {
        // An override whose RECURRENCE-ID is no occurrence of the series in its new zone is dropped and counted.
        var item = CalDav("""
            BEGIN:VEVENT
            UID:w2
            DTSTAMP:20260101T000000Z
            DTSTART;TZID=Europe/London:20261102T090000
            DTEND;TZID=Europe/London:20261102T100000
            RRULE:FREQ=WEEKLY;BYDAY=MO;COUNT=4
            SUMMARY:Standup
            END:VEVENT
            BEGIN:VEVENT
            UID:w2
            DTSTAMP:20260101T000000Z
            RECURRENCE-ID:20261116T093000Z
            DTSTART;TZID=Europe/London:20261116T110000
            DTEND;TZID=Europe/London:20261116T120000
            SUMMARY:Orphan
            END:VEVENT

            """);
        var nov9 = new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);
        var f = CalDavRecurrence.ReadOccurrence(item, nov9, London);

        var edited = CalDavRecurrence.EditSeries(item, nov9,
            f with { Start = new DateTime(2026, 11, 9, 9, 0, 0), End = new DateTime(2026, 11, 9, 10, 0, 0), StartZone = NewYork }, London, Now)!;

        Assert.Equal(1, CalDavRecurrence.LostOverrides(item, edited));
        Assert.DoesNotContain("Orphan", edited.RawPayload);
    }

    [Fact]
    public void GraphSeries_NewZone_MovesTheMasterAndTheRecurrenceZone()
    {
        var master = Graph("""
            {"id":"m1","type":"seriesMaster","subject":"Sync","originalStartTimeZone":"GMT Standard Time","originalEndTimeZone":"GMT Standard Time",
             "recurrence":{"pattern":{"type":"weekly","interval":1,"daysOfWeek":["tuesday"],"firstDayOfWeek":"sunday"},
               "range":{"type":"endDate","startDate":"2026-11-10","endDate":"2026-12-15","recurrenceTimeZone":"GMT Standard Time"}},
             "start":{"dateTime":"2026-11-10T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-10T10:00:00","timeZone":"UTC"}}
            """);
        var nov17 = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
        var f = GraphRecurrence.Occurrence(master, nov17, London);

        var edited = GraphRecurrence.EditSeries(master, nov17, f with { StartZone = NewYork }, London)!;

        var patch = JsonNode.Parse(GraphEvents.PatchBody(edited.RawPayload))!.AsObject();
        Assert.Equal(["end", "isAllDay", "recurrence", "start"], patch.Select(p => p.Key).Order());
        Assert.Equal(("2026-11-10T09:00:00", "Eastern Standard Time"), Graph(patch["start"]));
        Assert.Equal(("2026-11-10T10:00:00", "Eastern Standard Time"), Graph(patch["end"]));
        var range = patch["recurrence"]!["range"]!;
        Assert.Equal(("2026-11-10", "2026-12-15", "Eastern Standard Time"),
            (range["startDate"]!.GetValue<string>(), range["endDate"]!.GetValue<string>(), range["recurrenceTimeZone"]!.GetValue<string>()));
        Assert.Equal(["tuesday"], patch["recurrence"]!["pattern"]!["daysOfWeek"]!.AsArray().Select(d => d!.GetValue<string>()));
    }

    [Fact]
    public void Transfer_KeepsAChosenZone_BothWays()
    {
        var fields = Fields(Nov10At9, new DateTime(2026, 11, 10, 15, 0, 0), NewYork, GmtWindows);
        var caldav = CalDavEvents.Create(Radicale, fields, London, Now) with { Id = "i1" };
        var graph = GraphEvents.Create(Ms365, fields, London);

        var toGraph = EventTransfer.Move(caldav, false, Ms365, true, London, Now).Moved;
        var toCalDav = EventTransfer.Move(graph, true, Radicale, false, London, Now).Moved;

        Assert.Equal(("2026-11-10T09:00:00", "Eastern Standard Time"), Graph(Json(toGraph)["start"]));
        Assert.Equal(("2026-11-10T15:00:00", "GMT Standard Time"), Graph(Json(toGraph)["end"]));
        Assert.Contains("DTSTART;TZID=America/New_York:20261110T090000", toCalDav.RawPayload);
        Assert.Contains("DTEND;TZID=Europe/London:20261110T150000", toCalDav.RawPayload);
        Assert.Equal((caldav.StartUtc, caldav.EndUtc), (toGraph.StartUtc, toGraph.EndUtc));
        Assert.Equal((caldav.StartUtc, caldav.EndUtc), (toCalDav.StartUtc, toCalDav.EndUtc));
    }

    // ---- This event and This and following (slice 4) ----

    private static Item LondonMondays() => CalDav("""
        BEGIN:VEVENT
        UID:w3
        DTSTAMP:20260101T000000Z
        DTSTART;TZID=Europe/London:20261102T090000
        DTEND;TZID=Europe/London:20261102T100000
        RRULE:FREQ=WEEKLY;BYDAY=MO;UNTIL=20261214T090000Z
        EXDATE;TZID=Europe/London:20261130T090000
        SUMMARY:Standup
        END:VEVENT

        """);

    private static List<(DateTimeOffset Start, DateTimeOffset End, DateTimeOffset Recurrence)> Expand(Item item) =>
        OccurrenceExpander.Expand(Calendar.Load(item.RawPayload)!, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero), London).OrderBy(o => o.Start).ToList();

    private static readonly DateTimeOffset Nov9 = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CalDavOccurrence_NewZone_GetsAnOverrideInIt_AndTheOthersStay()
    {
        var item = LondonMondays();
        var f = CalDavRecurrence.ReadOccurrence(item, Nov9, London);

        var edited = CalDavRecurrence.EditOccurrence(item, Nov9, f with { StartZone = NewYork }, London, Now)!;

        var data = edited.RawPayload;
        Assert.Contains("RECURRENCE-ID;TZID=Europe/London:20261109T090000", data);
        Assert.Contains("DTSTART;TZID=America/New_York:20261109T090000", data);
        Assert.Contains("DTEND;TZID=America/New_York:20261109T100000", data);
        Assert.Contains("DTSTART;TZID=Europe/London:20261102T090000", data);
        Assert.Single(Calendar.Load(data)!.TimeZones, z => z.TzId == "America/New_York");
        var occurrences = Expand(edited);
        Assert.Equal([2, 9, 16, 23, 7, 14], occurrences.Select(o => o.Recurrence.Day));
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 14, 0, 0, TimeSpan.Zero), occurrences.Single(o => o.Recurrence == Nov9).Start);
        Assert.All(occurrences.Where(o => o.Recurrence != Nov9), o => Assert.Equal((9, 10), (o.Start.Hour, o.End.Hour)));
        // The editor shows the occurrence's own zone, and the series' for the whole series.
        Assert.Equal((NewYork.Id, null, false), Ids(TimeZoneLabels.OwnZones(edited, London, Nov9)));
        Assert.Equal((GmtWindows.Id, null, false), Ids(TimeZoneLabels.OwnZones(edited, London)));
    }

    [Fact]
    public void CalDavOccurrence_SeparateEndZone()
    {
        var item = LondonMondays();
        var f = CalDavRecurrence.ReadOccurrence(item, Nov9, London);

        var edited = CalDavRecurrence.EditOccurrence(item, Nov9,
            f with { Start = new DateTime(2026, 11, 9, 9, 0, 0), End = new DateTime(2026, 11, 9, 15, 0, 0), StartZone = NewYork, EndZone = London },
            London, Now)!;

        Assert.Contains("DTSTART;TZID=America/New_York:20261109T090000", edited.RawPayload);
        Assert.Contains("DTEND;TZID=Europe/London:20261109T150000", edited.RawPayload);
        var occurrence = Expand(edited).Single(o => o.Recurrence == Nov9);
        Assert.Equal((Nov9.AddHours(5), Nov9.AddHours(6)), (occurrence.Start, occurrence.End));
        Assert.Equal((NewYork.Id, GmtWindows.Id, false), Ids(TimeZoneLabels.OwnZones(edited, London, Nov9)));
    }

    [Fact]
    public void CalDavFollowing_SydneyToLondon_AcrossDst_SplitsAndConvertsTheNewSeries()
    {
        // Mondays 09:00 Sydney from 7 Sep (AEST) to 30 Nov; Sydney goes to AEDT on 4 Oct, London to GMT on 25 Oct.
        // 26 Oct deleted, 9 Nov changed (title only). Split at 12 Oct (09:00 AEDT = 11 Oct 22:00 UTC) into London.
        var item = CalDav("""
            BEGIN:VEVENT
            UID:s1
            DTSTAMP:20260101T000000Z
            DTSTART;TZID=Australia/Sydney:20260907T090000
            DTEND;TZID=Australia/Sydney:20260907T100000
            RRULE:FREQ=WEEKLY;BYDAY=MO;UNTIL=20261129T220000Z
            EXDATE;TZID=Australia/Sydney:20261026T090000
            SUMMARY:Standup
            END:VEVENT
            BEGIN:VEVENT
            UID:s1
            DTSTAMP:20260101T000000Z
            RECURRENCE-ID;TZID=Australia/Sydney:20261109T090000
            DTSTART;TZID=Australia/Sydney:20261109T090000
            DTEND;TZID=Australia/Sydney:20261109T100000
            SUMMARY:Standup (notes)
            END:VEVENT

            """);
        var oct12 = new DateTimeOffset(2026, 10, 11, 22, 0, 0, TimeSpan.Zero);
        var f = CalDavRecurrence.ReadOccurrence(item, oct12, London);
        var fields = f with { Start = new DateTime(2026, 10, 12, 9, 0, 0), End = new DateTime(2026, 10, 12, 10, 0, 0), StartZone = London };

        var split = CalDavRecurrence.SplitSeries(item, oct12, fields, Radicale, London, Now)!;

        // The original keeps its zone and ends before the split.
        var original = split.Original.RawPayload;
        Assert.Contains("DTSTART;TZID=Australia/Sydney:20260907T090000", original);
        Assert.Contains("UNTIL=20261011T215959Z", original);
        Assert.DoesNotContain("EXDATE", original);
        Assert.DoesNotContain("RECURRENCE-ID", original);
        var before = Expand(split.Original);
        Assert.Equal(5, before.Count);
        Assert.All(before, o => Assert.Equal(9, TimeZoneInfo.ConvertTime(o.Start, Sydney).Hour));

        // The new series is in London at 09:00, its EXDATE, RECURRENCE-ID and UNTIL converted.
        var next = split.Following!.RawPayload;
        Assert.Contains("DTSTART;TZID=Europe/London:20261012T090000", next);
        Assert.Contains("DTEND;TZID=Europe/London:20261012T100000", next);
        Assert.Contains("EXDATE;TZID=Europe/London:20261026T090000", next);
        Assert.Contains("RECURRENCE-ID;TZID=Europe/London:20261109T090000", next);
        Assert.Contains("UNTIL=20261130T090000Z", next);
        Assert.Single(Calendar.Load(next)!.TimeZones, z => z.TzId == "Europe/London");
        var after = Expand(split.Following);
        Assert.Equal([12, 19, 2, 9, 16, 23, 30], after.Select(o => o.Start.Day));
        Assert.All(after, o => Assert.Equal((9, 10), (TimeZoneInfo.ConvertTime(o.Start, London).Hour, TimeZoneInfo.ConvertTime(o.End, London).Hour)));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero), split.Recurrence);
    }

    [Fact]
    public void CalDavFollowing_SeparateEndZone()
    {
        var item = LondonMondays();
        var nov16 = Nov9.AddDays(7);
        var f = CalDavRecurrence.ReadOccurrence(item, nov16, London);

        var split = CalDavRecurrence.SplitSeries(item, nov16,
            f with { Start = new DateTime(2026, 11, 16, 9, 0, 0), End = new DateTime(2026, 11, 16, 15, 0, 0), StartZone = NewYork, EndZone = London },
            Radicale, London, Now)!;

        Assert.Contains("DTSTART;TZID=Europe/London:20261102T090000", split.Original.RawPayload);
        Assert.Equal([2, 9], Expand(split.Original).Select(o => o.Start.Day));
        var next = split.Following!.RawPayload;
        Assert.Contains("DTSTART;TZID=America/New_York:20261116T090000", next);
        Assert.Contains("DTEND;TZID=Europe/London:20261116T150000", next);
        Assert.Contains("EXDATE;TZID=America/New_York:20261130T090000", next);
        var after = Expand(split.Following);
        Assert.Equal([16, 23, 7, 14], after.Select(o => o.Start.Day));
        Assert.All(after, o => Assert.Equal((14, 15), (o.Start.Hour, o.End.Hour)));
    }

    [Fact]
    public void CalDavFollowing_NewZone_OnASeriesWithListedDates_IsRefused()
    {
        var item = CalDav("""
            BEGIN:VEVENT
            UID:w4
            DTSTAMP:20260101T000000Z
            DTSTART;TZID=Europe/London:20261102T090000
            DTEND;TZID=Europe/London:20261102T100000
            RRULE:FREQ=WEEKLY;BYDAY=MO;COUNT=4
            RDATE;TZID=Europe/London:20261104T090000
            SUMMARY:Standup
            END:VEVENT

            """);
        var f = CalDavRecurrence.ReadOccurrence(item, Nov9, London);

        var ex = Assert.Throws<EditRefusedException>(() => CalDavRecurrence.SplitSeries(item, Nov9, f with { StartZone = NewYork }, Radicale, London, Now));
        Assert.Contains("timezone", ex.Message);
    }

    private static readonly Item GraphMaster = Graph("""
        {"id":"m1","type":"seriesMaster","subject":"Sync","originalStartTimeZone":"GMT Standard Time","originalEndTimeZone":"GMT Standard Time",
         "recurrence":{"pattern":{"type":"weekly","interval":1,"daysOfWeek":["tuesday"],"firstDayOfWeek":"sunday"},
           "range":{"type":"endDate","startDate":"2026-11-10","endDate":"2026-12-15","recurrenceTimeZone":"GMT Standard Time"}},
         "start":{"dateTime":"2026-11-10T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-10T10:00:00","timeZone":"UTC"}}
        """);

    [Fact]
    public void GraphOccurrence_NewZone_PatchesTheInstanceOnly()
    {
        var occurrence = Graph("""
            {"id":"o1","type":"occurrence","seriesMasterId":"m1","originalStartTimeZone":"GMT Standard Time","originalEndTimeZone":"GMT Standard Time",
             "start":{"dateTime":"2026-11-17T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-17T10:00:00","timeZone":"UTC"}}
            """);
        var f = GraphEventMapper.Read(occurrence, GraphMaster, London);

        var edited = GraphEvents.Edit(occurrence, GraphMaster, f with { StartZone = NewYork }, London)!;
        var separate = GraphEvents.Edit(occurrence, GraphMaster,
            f with { Start = new DateTime(2026, 11, 17, 9, 0, 0), End = new DateTime(2026, 11, 17, 15, 0, 0), StartZone = NewYork, EndZone = London }, London)!;

        var patch = JsonNode.Parse(GraphEvents.PatchBody(edited.RawPayload))!.AsObject();
        Assert.Equal(["end", "isAllDay", "start"], patch.Select(p => p.Key).Order());
        Assert.Equal(("2026-11-17T09:00:00", "Eastern Standard Time"), Graph(patch["start"]));
        Assert.Equal(("2026-11-17T10:00:00", "Eastern Standard Time"), Graph(patch["end"]));
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 14, 0, 0, TimeSpan.Zero), edited.StartUtc);
        Assert.Equal((NewYork.Id, null, false), Ids(TimeZoneLabels.OwnZones(edited, London)));
        var both = JsonNode.Parse(GraphEvents.PatchBody(separate.RawPayload))!.AsObject();
        Assert.Equal(("2026-11-17T09:00:00", "Eastern Standard Time"), Graph(both["start"]));
        Assert.Equal(("2026-11-17T15:00:00", "GMT Standard Time"), Graph(both["end"]));
        Assert.Equal((NewYork.Id, GmtWindows.Id, false), Ids(TimeZoneLabels.OwnZones(separate, London)));
    }

    [Fact]
    public void GraphFollowing_NewZone_TheNewSeriesIsInIt_AndTheOriginalKeepsItsOwn()
    {
        var nov24 = new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
        var f = GraphRecurrence.Occurrence(GraphMaster, nov24, London);

        var (original, following) = GraphRecurrence.SplitSeries(GraphMaster, nov24, f with { StartZone = NewYork }, Ms365, London)!.Value;
        var (_, separate) = GraphRecurrence.SplitSeries(GraphMaster, nov24,
            f with { End = f.Start.AddHours(6), StartZone = NewYork, EndZone = London }, Ms365, London)!.Value;

        var truncated = JsonNode.Parse(GraphEvents.PatchBody(original.RawPayload))!.AsObject();
        Assert.Equal(["recurrence"], truncated.Select(p => p.Key));
        Assert.Equal(("2026-11-23", "GMT Standard Time"),
            (truncated["recurrence"]!["range"]!["endDate"]!.GetValue<string>(), truncated["recurrence"]!["range"]!["recurrenceTimeZone"]!.GetValue<string>()));
        var next = Json(following);
        Assert.Equal(("2026-11-24T09:00:00", "Eastern Standard Time"), Graph(next["start"]));
        Assert.Equal(("2026-11-24T10:00:00", "Eastern Standard Time"), Graph(next["end"]));
        var range = next["recurrence"]!["range"]!;
        Assert.Equal(("2026-11-24", "2026-12-15", "Eastern Standard Time"),
            (range["startDate"]!.GetValue<string>(), range["endDate"]!.GetValue<string>(), range["recurrenceTimeZone"]!.GetValue<string>()));
        Assert.Equal(["tuesday"], next["recurrence"]!["pattern"]!["daysOfWeek"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.Equal(nov24.AddHours(5), following.StartUtc);
        Assert.Equal(("2026-11-24T15:00:00", "GMT Standard Time"), Graph(Json(separate)["end"]));
        Assert.Equal(("2026-11-24T09:00:00", "Eastern Standard Time"), Graph(Json(separate)["start"]));
    }

    [Fact]
    public void GraphFollowing_SydneyToLondon_AcrossDst()
    {
        // Mondays 09:00 Sydney from 7 Sep (23:00 UTC the day before, AEST); split at 12 Oct (AEDT: 11 Oct 22:00 UTC).
        var master = Graph("""
            {"id":"m2","type":"seriesMaster","subject":"Standup","originalStartTimeZone":"AUS Eastern Standard Time","originalEndTimeZone":"AUS Eastern Standard Time",
             "recurrence":{"pattern":{"type":"weekly","interval":1,"daysOfWeek":["monday"],"firstDayOfWeek":"sunday"},
               "range":{"type":"noEnd","startDate":"2026-09-07","recurrenceTimeZone":"AUS Eastern Standard Time"}},
             "start":{"dateTime":"2026-09-06T23:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-09-07T00:00:00","timeZone":"UTC"}}
            """);
        var oct12 = new DateTimeOffset(2026, 10, 11, 22, 0, 0, TimeSpan.Zero);
        var f = GraphRecurrence.Occurrence(master, oct12, London);

        var (original, following) = GraphRecurrence.SplitSeries(master, oct12,
            f with { Start = new DateTime(2026, 10, 12, 9, 0, 0), End = new DateTime(2026, 10, 12, 10, 0, 0), StartZone = London }, Ms365, London)!.Value;

        var old = Json(original);
        Assert.Equal("2026-10-11", old["recurrence"]!["range"]!["endDate"]!.GetValue<string>());
        Assert.Equal("AUS Eastern Standard Time", old["recurrence"]!["range"]!["recurrenceTimeZone"]!.GetValue<string>());
        Assert.Equal("AUS Eastern Standard Time", old["originalStartTimeZone"]!.GetValue<string>());
        var next = Json(following);
        Assert.Equal(("2026-10-12T09:00:00", "GMT Standard Time"), Graph(next["start"]));
        Assert.Equal(("2026-10-12", "GMT Standard Time"),
            (next["recurrence"]!["range"]!["startDate"]!.GetValue<string>(), next["recurrence"]!["range"]!["recurrenceTimeZone"]!.GetValue<string>()));
        Assert.Equal(["monday"], next["recurrence"]!["pattern"]!["daysOfWeek"]!.AsArray().Select(d => d!.GetValue<string>()));
        // 09:00 BST.
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero), following.StartUtc);
    }

    private static (string? Start, string? End, bool Floating) Ids((TimeZoneInfo? Start, TimeZoneInfo? End, bool Floating) zones) =>
        (zones.Start is { } s ? TimeZoneLabels.WindowsId(s) : null, zones.End is { } e ? TimeZoneLabels.WindowsId(e) : null, zones.Floating);
}
