using System.Globalization;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Core.Tests;

/// <summary>The secondary ruler's labels and note (timezone-handling R8) and the original-zone time (R9).</summary>
public sealed class TimeZoneLabelsTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("AUS Eastern Standard Time");
    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
    private static readonly DateOnly NotShown = new(2026, 11, 11);

    private static RulerLabels Ruler(TimeZoneInfo zone, TimeZoneInfo display, DateOnly first, DateOnly today) =>
        SecondaryRuler.Build(zone, display, first, 7, today, Culture);

    // ---- Ruler (R8) ----

    [Fact]
    public void Ruler_AWeekWithNoChange_HasNoNote()
    {
        var r = Ruler(Sydney, London, new DateOnly(2026, 11, 9), NotShown);
        Assert.Equal(("Sydney", "20:00", "08:00", null), (r.Name, r.Hours[9], r.Hours[21], r.Note));
    }

    [Fact]
    public void Ruler_HalfHourZone_ShowsTheMinutes()
    {
        var r = Ruler(India, London, new DateOnly(2026, 11, 9), NotShown);
        Assert.Equal(("14:30", "01:30", null), (r.Hours[9], r.Hours[20], r.Note));
    }

    [Fact]
    public void Ruler_SydneyEndsDstInApril_NotesTheSunday()
    {
        // London is on BST from 29 March; Sydney leaves daylight time on Sunday 5 April.
        var r = Ruler(Sydney, London, new DateOnly(2026, 3, 30), NotShown);
        Assert.Equal(("19:00", "−1 h from Sun"), (r.Hours[9], r.Note));
    }

    [Fact]
    public void Ruler_LondonStartsBstMidWeek_UsesTodaysOffset()
    {
        // Today (Wednesday) is visible: Sydney +11, London +0; from Sunday 29 March London is +1.
        var r = Ruler(Sydney, London, new DateOnly(2026, 3, 23), new DateOnly(2026, 3, 25));
        Assert.Equal(("12:00", "−1 h from Sun"), (r.Hours[1], r.Note));
    }

    [Fact]
    public void Ruler_ChangeBeforeToday_SaysUntil()
    {
        // Thursday 26 March to Wednesday 1 April, today Tuesday: the days before Sunday 29 March are 1 h more.
        var r = Ruler(Sydney, London, new DateOnly(2026, 3, 26), new DateOnly(2026, 3, 31));
        Assert.Equal(("19:00", "+1 h until Sat"), (r.Hours[9], r.Note));
    }

    [Fact]
    public void Ruler_October_BothChanges()
    {
        // Sydney starts daylight time on Sunday 4 October; London leaves BST on Sunday 25 October.
        Assert.Equal("+1 h from Sun", Ruler(Sydney, London, new DateOnly(2026, 9, 28), NotShown).Note);
        Assert.Equal("+1 h from Sun", Ruler(Sydney, London, new DateOnly(2026, 10, 19), NotShown).Note);
        var after = Ruler(Sydney, London, new DateOnly(2026, 10, 25), NotShown);
        Assert.Equal(("20:00", null), (after.Hours[9], after.Note));
    }

    [Fact]
    public void Ruler_DisplayAhead_LabelsWrapRoundMidnight()
    {
        var r = Ruler(London, Sydney, new DateOnly(2026, 11, 9), NotShown);
        Assert.Equal(("London", "22:00", "13:00"), (r.Name, r.Hours[9], r.Hours[0]));
    }

    [Fact]
    public void ShortName_CityOrUtc()
    {
        Assert.Equal("UTC", TimeZoneLabels.ShortName(TimeZoneInfo.Utc));
        Assert.Equal("New York", TimeZoneLabels.ShortName(TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")));
        Assert.Equal("Sydney", TimeZoneLabels.ShortName(TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney")));
    }

    // ---- Original-zone time (R9) ----

    private static DateTimeOffset Utc(int month, int day, int hour) => new(2026, month, day, hour, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Eastern Standard Time")]
    [InlineData("America/New_York")]
    public void Original_NewYorkEventSeenInLondon(string zone)
    {
        Assert.Equal("09:00 – 10:00 New York time",
            TimeZoneLabels.OriginalTime(Utc(11, 16, 14), Utc(11, 16, 15), zone, null, London, Culture));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("GMT Standard Time")]
    [InlineData("Europe/London")]
    [InlineData("UTC")]
    [InlineData("tzone://Microsoft/Custom")]
    [InlineData("Not/AZone")]
    public void Original_DisplayZoneUtcOrUnknown_GivesNothing(string? zone)
    {
        Assert.Null(TimeZoneLabels.OriginalTime(Utc(11, 16, 14), Utc(11, 16, 15), zone, null, London, Culture));
    }

    [Fact]
    public void Original_SeparateEndZone_ShowsBoth_WithTheDayWhenItDiffers()
    {
        // Sydney Monday 09:00 (Sunday 22:00 in London) to London Monday 22:00.
        Assert.Equal("Mon 09:00 Sydney – 22:00 London",
            TimeZoneLabels.OriginalTime(Utc(11, 15, 22), Utc(11, 16, 22), "AUS Eastern Standard Time", "GMT Standard Time", London, Culture));
        Assert.Equal("09:00 Sydney – 22:00 London",
            TimeZoneLabels.OriginalTime(Utc(11, 15, 22), Utc(11, 16, 22), "Australia/Sydney", "Europe/London", Sydney, Culture));
    }

    [Fact]
    public void EventZones_GraphAndCalDav()
    {
        var graph = new Item("i1", "c1", ItemKind.Event, "r1",
            """{"originalStartTimeZone":"AUS Eastern Standard Time","originalEndTimeZone":"GMT Standard Time"}""");
        Assert.Equal(("AUS Eastern Standard Time", "GMT Standard Time"), TimeZoneLabels.EventZones(graph));

        var caldav = new Item("i2", "c1", ItemKind.Event, "uid", Ics("DTSTART;TZID=Australia/Sydney:20261116T090000", "DTEND;TZID=Europe/London:20261116T220000"));
        Assert.Equal(("Australia/Sydney", "Europe/London"), TimeZoneLabels.EventZones(caldav));

        var floating = new Item("i3", "c1", ItemKind.Event, "uid", Ics("DTSTART:20261116T090000", "DTEND:20261116T100000"));
        Assert.Equal((null, null), TimeZoneLabels.EventZones(floating));
    }

    private static string Ics(string start, string end) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\nBEGIN:VEVENT\r\nUID:uid\r\nDTSTAMP:20261101T000000Z\r\n{start}\r\n{end}\r\nSUMMARY:Flight\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
}
