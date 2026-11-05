using Ical.Net;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>Editor writes into iCalendar (calendar-views R6 interim; sync-radicale R7, R8).</summary>
public sealed class CalDavEventsTests
{
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 1, 0, 0, TimeSpan.Zero);
    private static readonly Collection Home = new("c1", "a1", CollectionKind.Calendar, "Home", "Navy", "Circle", true, false, "https://dav.example.test/ben/home/");

    private static string Ics(string body) =>
        ("BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Apple Inc.//macOS//EN\n" + body + "END:VCALENDAR\n").Replace("\n", "\r\n");

    private static Item Existing(string body) =>
        CalDavItemMapper.Map("https://dav.example.test/ben/home/x.ics", "\"e1\"", Ics(body), Sydney)! with { Id = "i1", CollectionId = "c1" };

    private const string Rich = """
        BEGIN:VTIMEZONE
        TZID:Europe/London
        X-LIC-LOCATION:Europe/London
        BEGIN:STANDARD
        DTSTART:19701025T020000
        TZOFFSETFROM:+0100
        TZOFFSETTO:+0000
        RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU
        END:STANDARD
        BEGIN:DAYLIGHT
        DTSTART:19700329T010000
        TZOFFSETFROM:+0000
        TZOFFSETTO:+0100
        RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU
        END:DAYLIGHT
        END:VTIMEZONE
        BEGIN:VEVENT
        UID:x1
        DTSTAMP:20260101T000000Z
        DTSTART;TZID=Europe/London:20261110T090000
        DTEND;TZID=Europe/London:20261110T100000
        SUMMARY:Planning
        LOCATION:Room 1
        GEO:51.5;-0.12
        X-APPLE-TRAVEL-ADVISORY-BEHAVIOR:AUTOMATIC
        SEQUENCE:2
        BEGIN:VALARM
        ACTION:DISPLAY
        TRIGGER:-PT15M
        DESCRIPTION:Reminder
        END:VALARM
        END:VEVENT

        """;

    [Fact]
    public void Read_GivesWallClockInWindowsZone()
    {
        var f = CalDavEvents.Read(Existing(Rich), Sydney);

        // 09:00 London (GMT) on 10 Nov is 20:00 in Sydney (AEDT).
        Assert.Equal(("Planning", false, new DateTime(2026, 11, 10, 20, 0, 0), new DateTime(2026, 11, 10, 21, 0, 0), "Room 1"),
            (f.Title, f.AllDay, f.Start, f.End, f.Location));
    }

    [Fact]
    public void EditTitle_PreservesUnknownPropertiesAlarmAndTimes()
    {
        var item = Existing(Rich);
        var f = CalDavEvents.Read(item, Sydney);

        var edited = CalDavEvents.Edit(item, f with { Title = "Planning v2" }, Sydney, Now)!;

        var data = edited.RawPayload;
        Assert.Contains("SUMMARY:Planning v2", data);
        // Ical.Net writes GEO with six decimals; the value is unchanged.
        Assert.Contains("GEO:51.500000;-0.120000", data);
        Assert.Contains("X-APPLE-TRAVEL-ADVISORY-BEHAVIOR:AUTOMATIC", data);
        Assert.Contains("BEGIN:VALARM", data);
        Assert.Contains("X-LIC-LOCATION:Europe/London", data);
        Assert.Contains("DTSTART;TZID=Europe/London:20261110T090000", data);
        Assert.Contains("SEQUENCE:3", data);
        Assert.Equal(("i1", "c1", "https://dav.example.test/ben/home/x.ics", "\"e1\""), (edited.Id, edited.CollectionId, edited.RemoteId, edited.Etag));
        Assert.Equal("Planning v2", edited.Summary);
    }

    [Fact]
    public void EditTimes_WritesInEventsOwnZone()
    {
        var item = Existing(Rich);
        var f = CalDavEvents.Read(item, Sydney);

        var edited = CalDavEvents.Edit(item, f with { Start = f.Start.AddHours(2), End = f.End.AddHours(3) }, Sydney, Now)!;

        Assert.Contains("DTSTART;TZID=Europe/London:20261110T110000", edited.RawPayload);
        Assert.Contains("DTEND;TZID=Europe/London:20261110T130000", edited.RawPayload);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 11, 0, 0, TimeSpan.Zero), edited.StartUtc);
    }

    [Fact]
    public void EditFloatingAndUtcTimes_KeepTheirForm()
    {
        var floating = Existing("BEGIN:VEVENT\nUID:f\nDTSTAMP:20260101T000000Z\nDTSTART:20261110T090000\nDURATION:PT1H\nSUMMARY:F\nEND:VEVENT\n");
        var utc = Existing("BEGIN:VEVENT\nUID:u\nDTSTAMP:20260101T000000Z\nDTSTART:20261110T090000Z\nDTEND:20261110T100000Z\nSUMMARY:U\nEND:VEVENT\n");

        var f1 = CalDavEvents.Read(floating, Sydney);
        var e1 = CalDavEvents.Edit(floating, f1 with { End = f1.End.AddHours(1) }, Sydney, Now)!.RawPayload;
        var f2 = CalDavEvents.Read(utc, Sydney);
        var e2 = CalDavEvents.Edit(utc, f2 with { Start = f2.Start.AddHours(1), End = f2.End.AddHours(1) }, Sydney, Now)!.RawPayload;

        Assert.Contains("DTSTART:20261110T090000\r\n", e1);
        Assert.Contains("DTEND:20261110T110000\r\n", e1);
        Assert.DoesNotContain("DURATION", e1);
        Assert.Contains("DTSTART:20261110T100000Z", e2);
    }

    [Fact]
    public void MakeAllDay_WritesDates()
    {
        var item = Existing(Rich);
        var f = CalDavEvents.Read(item, Sydney);

        var edited = CalDavEvents.Edit(item, f with { AllDay = true, Start = new DateTime(2026, 11, 10), End = new DateTime(2026, 11, 12) }, Sydney, Now)!;

        Assert.Contains("DTSTART;VALUE=DATE:20261110", edited.RawPayload);
        Assert.Contains("DTEND;VALUE=DATE:20261112", edited.RawPayload);
        Assert.True(edited.AllDay);
        var back = CalDavEvents.Read(edited, Sydney);
        Assert.Equal((true, new DateTime(2026, 11, 10), new DateTime(2026, 11, 12)), (back.AllDay, back.Start, back.End));
    }

    [Fact]
    public void Create_UsesWindowsZoneWithDefinitionAndUidHref()
    {
        var item = CalDavEvents.Create(Home,
            new EventFields("Lunch", false, new DateTime(2026, 11, 6, 12, 30, 0), new DateTime(2026, 11, 6, 13, 30, 0), "Cafe", "Bring notes"),
            Sydney, Now);

        var calendar = Calendar.Load(item.RawPayload)!;
        var ev = Assert.Single(calendar.Events);
        Assert.Equal($"https://dav.example.test/ben/home/{ev.Uid}.ics", item.RemoteId);
        Assert.True(Guid.TryParse(ev.Uid, out _));
        Assert.Contains("DTSTART;TZID=Australia/Sydney:20261106T123000", item.RawPayload);
        Assert.Single(calendar.TimeZones, z => z.TzId == "Australia/Sydney");
        Assert.Equal(("c1", "Lunch", "Cafe", null), (item.CollectionId, item.Summary, item.Location, item.Etag));
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 1, 30, 0, TimeSpan.Zero), item.StartUtc);
        Assert.Equal("Bring notes", CalDavEvents.Read(item, Sydney).Description);
    }

    [Fact]
    public void NoChange_ReturnsNull()
    {
        var item = Existing(Rich);

        Assert.Null(CalDavEvents.Edit(item, CalDavEvents.Read(item, Sydney) with { Description = "" }, Sydney, Now));
    }

    [Fact]
    public void ClearingLocation_RemovesProperty()
    {
        var item = Existing(Rich);

        var edited = CalDavEvents.Edit(item, CalDavEvents.Read(item, Sydney) with { Location = "  " }, Sydney, Now)!;

        Assert.DoesNotContain("\nLOCATION", edited.RawPayload);
        Assert.Null(edited.Location);
    }
}
