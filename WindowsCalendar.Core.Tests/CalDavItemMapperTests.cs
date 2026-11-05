using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

public sealed class CalDavItemMapperTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private static string Ics(string component) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + component.Replace("\n", "\r\n") + "END:VCALENDAR\r\n";

    [Fact]
    public void RecurringEvent_UsesMasterAndKeepsPayload()
    {
        var data = Ics(
            """
            BEGIN:VEVENT
            UID:s1
            DTSTAMP:20260101T000000Z
            RECURRENCE-ID;TZID=Europe/London:20261026T090000
            DTSTART;TZID=Europe/London:20261026T140000
            DTEND;TZID=Europe/London:20261026T150000
            SUMMARY:Override
            END:VEVENT
            BEGIN:VEVENT
            UID:s1
            DTSTAMP:20260101T000000Z
            DTSTART;TZID=Europe/London:20261019T090000
            DTEND;TZID=Europe/London:20261019T100000
            RRULE:FREQ=WEEKLY
            SUMMARY:Standup
            LOCATION:Room 1
            STATUS:CONFIRMED
            LAST-MODIFIED:20261001T120000Z
            END:VEVENT

            """);

        var item = CalDavItemMapper.Map("https://dav.example.test/ben/personal/s1.ics", "\"e1\"", data, London)!;

        Assert.Equal(ItemKind.Event, item.Kind);
        Assert.Equal(("Standup", "Room 1", "CONFIRMED", "Europe/London"), (item.Summary, item.Location, item.Status, item.TimeZone));
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero), item.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero), item.EndUtc);
        Assert.True(item.IsRecurring);
        Assert.False(item.AllDay);
        Assert.Equal(("https://dav.example.test/ben/personal/s1.ics", "\"e1\"", data), (item.RemoteId, item.Etag, item.RawPayload));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), item.LastModifiedUtc);
    }

    [Fact]
    public void AllDayEvent_IsMarkedAllDay()
    {
        var item = CalDavItemMapper.Map("h", "e", Ics("BEGIN:VEVENT\nUID:d\nDTSTAMP:20260101T000000Z\nDTSTART;VALUE=DATE:20261201\nEND:VEVENT\n"), London)!;

        Assert.True(item.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), item.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 12, 2, 0, 0, 0, TimeSpan.Zero), item.EndUtc);
    }

    [Fact]
    public void Todo_MapsDueAndCompleted()
    {
        var item = CalDavItemMapper.Map("h", "e", Ics(
            "BEGIN:VTODO\nUID:t\nDTSTAMP:20260101T000000Z\nDUE;VALUE=DATE:20261112\nCOMPLETED:20261110T100000Z\nSTATUS:COMPLETED\nSUMMARY:Pay\nEND:VTODO\n"),
            London)!;

        Assert.Equal(ItemKind.Task, item.Kind);
        Assert.Equal(("Pay", "COMPLETED"), (item.Summary, item.Status));
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero), item.DueUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero), item.CompletedUtc);
        Assert.True(item.AllDay);
    }

    [Theory]
    [InlineData("not ical")]
    [InlineData("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\nBEGIN:VJOURNAL\r\nUID:j\r\nDTSTAMP:20260101T000000Z\r\nEND:VJOURNAL\r\nEND:VCALENDAR\r\n")]
    public void UnreadableOrOtherComponent_IsNull(string data) =>
        Assert.Null(CalDavItemMapper.Map("h", "e", data, London));
}
