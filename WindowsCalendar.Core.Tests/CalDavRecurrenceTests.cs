using Ical.Net;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>This-occurrence and entire-series edits of recurring CalDAV events (recurring-events interim, R4, R5).</summary>
public sealed class CalDavRecurrenceTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 1, 0, 0, TimeSpan.Zero);

    // Mondays 09:00–10:00 London from 2 Nov 2026 (GMT, so wall time = UTC).
    private const string Weekly = """
        BEGIN:VEVENT
        UID:w1
        DTSTAMP:20260101T000000Z
        DTSTART;TZID=Europe/London:20261102T090000
        DTEND;TZID=Europe/London:20261102T100000
        RRULE:FREQ=WEEKLY;BYDAY=MO
        SUMMARY:Standup
        SEQUENCE:1
        END:VEVENT

        """;

    private static readonly DateTimeOffset Nov9 = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private static Item Existing(string body) =>
        CalDavItemMapper.Map("https://dav.example.test/ben/home/x.ics", "\"e1\"",
            ("BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Test//EN\n" + body + "END:VCALENDAR\n").Replace("\n", "\r\n"), London)!
        with
        { Id = "i1", CollectionId = "c1" };

    private static List<(DateTimeOffset Start, DateTimeOffset End, DateTimeOffset Recurrence)> Occurrences(Item item) =>
        OccurrenceExpander.Expand(Calendar.Load(item.RawPayload)!, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), London);

    private static DateTime Wall(int month, int day, int hour) => new(2026, month, day, hour, 0, 0);

    [Fact]
    public void ReadOccurrence_GivesTheSeriesAtThatOccurrence()
    {
        var f = CalDavRecurrence.ReadOccurrence(Existing(Weekly), Nov9, London);

        Assert.Equal("Standup", f.Title);
        Assert.Equal(Wall(11, 9, 9), f.Start);
        Assert.Equal(Wall(11, 9, 10), f.End);
    }

    [Fact]
    public void EditOccurrence_AddsOverrideInTheMastersForm()
    {
        var item = Existing(Weekly);
        var f = CalDavRecurrence.ReadOccurrence(item, Nov9, London);

        var edited = CalDavRecurrence.EditOccurrence(item, Nov9, f with { Title = "Retro", Start = Wall(11, 9, 11), End = Wall(11, 9, 12) }, London, Now)!;

        var calendar = Calendar.Load(edited.RawPayload)!;
        Assert.Equal(2, calendar.Events.Count);
        var ov = calendar.Events.Single(e => e.RecurrenceIdentifier is not null);
        Assert.Equal("Europe/London", ov.RecurrenceIdentifier!.StartTime.TzId);
        Assert.Equal(Wall(11, 9, 9), ov.RecurrenceIdentifier.StartTime.Value);
        Assert.Equal("w1", ov.Uid);
        Assert.Empty(ov.RecurrenceRules);
        var occurrences = Occurrences(edited);
        Assert.Equal(5, occurrences.Count);
        Assert.Contains(occurrences, o => o.Recurrence == Nov9 && o.Start == Nov9.AddHours(2));
        Assert.Equal("Retro", CalDavRecurrence.ReadOccurrence(edited, Nov9, London).Title);
        Assert.Equal("Standup", CalDavRecurrence.ReadOccurrence(edited, Nov9.AddDays(7), London).Title);
    }

    [Fact]
    public void EditOccurrence_AgainReplacesTheSameOverride()
    {
        var item = Existing(Weekly);
        var f = CalDavRecurrence.ReadOccurrence(item, Nov9, London);
        var once = CalDavRecurrence.EditOccurrence(item, Nov9, f with { Title = "A" }, London, Now)!;

        var twice = CalDavRecurrence.EditOccurrence(once, Nov9, f with { Title = "B" }, London, Now)!;

        Assert.Equal(2, Calendar.Load(twice.RawPayload)!.Events.Count);
        Assert.Equal("B", CalDavRecurrence.ReadOccurrence(twice, Nov9, London).Title);
    }

    [Fact]
    public void EditOccurrence_NoChange_ReturnsNull()
    {
        var item = Existing(Weekly);
        Assert.Null(CalDavRecurrence.EditOccurrence(item, Nov9, CalDavRecurrence.ReadOccurrence(item, Nov9, London), London, Now));
    }

    [Fact]
    public void EditOccurrence_OntoTheNextOccurrence_IsRefused()
    {
        var item = Existing(Weekly);
        var f = CalDavRecurrence.ReadOccurrence(item, Nov9, London);

        Assert.Throws<EditRefusedException>(() =>
            CalDavRecurrence.EditOccurrence(item, Nov9, f with { Start = Wall(11, 16, 10), End = Wall(11, 16, 11) }, London, Now));
        Assert.Throws<EditRefusedException>(() =>
            CalDavRecurrence.EditOccurrence(item, Nov9, f with { Start = Wall(11, 2, 9), End = Wall(11, 2, 10) }, London, Now));
    }

    [Fact]
    public void DeleteOccurrence_AddsExdateAndDropsOverride()
    {
        var item = Existing(Weekly);
        var f = CalDavRecurrence.ReadOccurrence(item, Nov9, London);
        var overridden = CalDavRecurrence.EditOccurrence(item, Nov9, f with { Title = "Retro" }, London, Now)!;

        var deleted = CalDavRecurrence.DeleteOccurrence(overridden, Nov9, London, Now);

        var calendar = Calendar.Load(deleted.RawPayload)!;
        Assert.Single(calendar.Events);
        Assert.Contains(calendar.Events[0].ExceptionDates.GetAllDates(), d => d.Value == Wall(11, 9, 9) && d.TzId == "Europe/London");
        var occurrences = Occurrences(deleted);
        Assert.Equal(4, occurrences.Count);
        Assert.DoesNotContain(occurrences, o => o.Recurrence == Nov9);
    }

    [Fact]
    public void EditSeries_Text_GoesToMasterAndOverridesKeepTheirOwn()
    {
        var item = Existing(Weekly);
        var f = CalDavRecurrence.ReadOccurrence(item, Nov9, London);
        var overridden = CalDavRecurrence.EditOccurrence(item, Nov9, f with { Title = "Retro" }, London, Now)!;
        var other = Nov9.AddDays(7);

        var edited = CalDavRecurrence.EditSeries(overridden, other,
            CalDavRecurrence.ReadOccurrence(overridden, other, London) with { Title = "Daily sync", Location = "Room 2" }, London, Now)!;

        Assert.Equal("Daily sync", CalDavRecurrence.ReadOccurrence(edited, other, London).Title);
        Assert.Equal("Room 2", CalDavRecurrence.ReadOccurrence(edited, Nov9.AddDays(14), London).Location);
        Assert.Equal("Retro", CalDavRecurrence.ReadOccurrence(edited, Nov9, London).Title);
    }

    [Fact]
    public void EditSeries_LaterTime_MovesEveryOccurrenceWithExceptions()
    {
        var item = Existing(Weekly);
        var f = CalDavRecurrence.ReadOccurrence(item, Nov9, London);
        var overridden = CalDavRecurrence.EditOccurrence(item, Nov9, f with { Title = "Retro" }, London, Now)!;
        var withExdate = CalDavRecurrence.DeleteOccurrence(overridden, Nov9.AddDays(7), London, Now);

        var moved = CalDavRecurrence.EditSeries(withExdate, Nov9.AddDays(14),
            CalDavRecurrence.ReadOccurrence(withExdate, Nov9.AddDays(14), London) with { Start = Wall(11, 23, 10), End = Wall(11, 23, 12) },
            London, Now)!;

        var occurrences = Occurrences(moved);
        Assert.Equal(4, occurrences.Count);
        Assert.All(occurrences, o => Assert.Equal(10, o.Start.Hour));
        Assert.All(occurrences, o => Assert.Equal(TimeSpan.FromHours(2), o.End - o.Start));
        Assert.DoesNotContain(occurrences, o => o.Start.Day == 16);
        Assert.Equal("Retro", CalDavRecurrence.ReadOccurrence(moved, Nov9.AddHours(1), London).Title);
    }

    [Fact]
    public void EditSeries_ToTuesday_ShiftsWeekday()
    {
        var item = Existing(Weekly);

        var moved = CalDavRecurrence.EditSeries(item, Nov9,
            CalDavRecurrence.ReadOccurrence(item, Nov9, London) with { Start = Wall(11, 10, 9), End = Wall(11, 10, 10) }, London, Now)!;

        Assert.Contains("BYDAY=TU", moved.RawPayload);
        var to = CalDavRecurrence.MovedRecurrence(item, Nov9,
            CalDavRecurrence.ReadOccurrence(item, Nov9, London) with { Start = Wall(11, 10, 9), End = Wall(11, 10, 10) }, London);
        Assert.Equal(Nov9.AddDays(1), to);
        Assert.Equal(Wall(11, 10, 9), CalDavRecurrence.ReadOccurrence(moved, to, London).Start);
        Assert.All(Occurrences(moved), o => Assert.Equal(DayOfWeek.Tuesday, o.Start.DayOfWeek));
    }

    [Fact]
    public void EditSeries_AllDayToTimed_IsRefused()
    {
        var item = Existing(Weekly);
        Assert.Throws<EditRefusedException>(() => CalDavRecurrence.EditSeries(item, Nov9,
            CalDavRecurrence.ReadOccurrence(item, Nov9, London) with { AllDay = true, Start = Wall(11, 9, 0), End = Wall(11, 10, 0) },
            London, Now));
    }

    [Theory]
    [InlineData(3, 1, "BYMONTHDAY=4")]
    [InlineData(10, -2, "BYMONTHDAY=8")]
    public void ShiftRule_MonthDay_Shifts(int day, int days, string expected)
    {
        var item = Existing(Monthly($"BYMONTHDAY={day}", day));
        var rid = new DateTimeOffset(2026, 11, day, 9, 0, 0, TimeSpan.Zero);
        var f = CalDavRecurrence.ReadOccurrence(item, rid, London);

        var moved = CalDavRecurrence.EditSeries(item, rid, f with { Start = f.Start.AddDays(days), End = f.End.AddDays(days) }, London, Now)!;

        Assert.Contains(expected, moved.RawPayload);
    }

    [Theory]
    [InlineData("BYMONTHDAY=28")]
    [InlineData("BYDAY=TU,WE;BYSETPOS=-1")]
    public void ShiftRule_Unshiftable_IsRefused(string parts)
    {
        var item = Existing(Monthly(parts, 25));
        var rid = Occurrences(item)[0].Recurrence;
        var f = CalDavRecurrence.ReadOccurrence(item, rid, London);

        Assert.Throws<EditRefusedException>(() =>
            CalDavRecurrence.EditSeries(item, rid, f with { Start = f.Start.AddDays(1), End = f.End.AddDays(1) }, London, Now));
    }

    [Fact]
    public void ShiftRule_OrdinalWeekday_KeepsOrdinal()
    {
        var item = Existing(Monthly("BYDAY=2TU", 10));
        var rid = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        var f = CalDavRecurrence.ReadOccurrence(item, rid, London);

        var moved = CalDavRecurrence.EditSeries(item, rid, f with { Start = f.Start.AddDays(1), End = f.End.AddDays(1) }, London, Now)!;

        Assert.Contains("BYDAY=2WE", moved.RawPayload);
    }

    [Fact]
    public void AllDaySeries_OverrideUsesDate()
    {
        var item = Existing("""
            BEGIN:VEVENT
            UID:d1
            DTSTAMP:20260101T000000Z
            DTSTART;VALUE=DATE:20261102
            DTEND;VALUE=DATE:20261103
            RRULE:FREQ=DAILY;COUNT=5
            SUMMARY:Leave
            END:VEVENT

            """);
        var rid = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

        var edited = CalDavRecurrence.EditOccurrence(item, rid, CalDavRecurrence.ReadOccurrence(item, rid, London) with { Title = "Half day" }, London, Now)!;

        var ov = Calendar.Load(edited.RawPayload)!.Events.Single(e => e.RecurrenceIdentifier is not null);
        Assert.False(ov.RecurrenceIdentifier!.StartTime.HasTime);
        Assert.False(ov.DtStart!.HasTime);
        Assert.Contains(Occurrences(edited), o => o.Recurrence == rid);
        Assert.Equal(5, Occurrences(edited).Count);
    }

    [Theory]
    [InlineData("DTSTART:20261102T090000", null)]
    [InlineData("DTSTART:20261102T090000Z", "UTC")]
    public void FloatingAndUtcSeries_OverrideKeepsForm(string start, string? tzId)
    {
        var item = Existing($"""
            BEGIN:VEVENT
            UID:f1
            DTSTAMP:20260101T000000Z
            {start}
            DURATION:PT1H
            RRULE:FREQ=DAILY;COUNT=5
            SUMMARY:Run
            END:VEVENT

            """);
        var rid = new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

        var edited = CalDavRecurrence.EditOccurrence(item, rid, CalDavRecurrence.ReadOccurrence(item, rid, London) with { Title = "Walk" }, London, Now)!;

        var ov = Calendar.Load(edited.RawPayload)!.Events.Single(e => e.RecurrenceIdentifier is not null);
        Assert.Equal(tzId, ov.RecurrenceIdentifier!.StartTime.TzId);
        Assert.Equal(5, Occurrences(edited).Count);
        Assert.Equal("Walk", CalDavRecurrence.ReadOccurrence(edited, rid, London).Title);
    }

    [Fact]
    public void Describe_Weekly()
    {
        Assert.Equal("Repeats weekly on Monday", CalDavRecurrence.Describe(Existing(Weekly)));
        Assert.Null(CalDavRecurrence.Describe(Existing(Weekly.Replace("RRULE:FREQ=WEEKLY;BYDAY=MO\n", ""))));
    }

    // ---- This and following (R6) ----

    private static readonly Collection Home = new("c1", "a1", CollectionKind.Calendar, "Home", "Navy", "Circle", true, false, "https://dav.example.test/ben/home/");

    private static SplitResult Split(Item item, DateTimeOffset rid, Func<EventFields, EventFields> change) =>
        CalDavRecurrence.SplitSeries(item, rid, change(CalDavRecurrence.ReadOccurrence(item, rid, London)), Home, London, Now)!;

    [Fact]
    public void Split_EndsOriginalAndStartsNewSeries()
    {
        var item = Existing(Weekly);
        var nov16 = Nov9.AddDays(7);

        var split = Split(item, nov16, f => f with { Title = "New standup" });

        Assert.Contains("UNTIL=20261116T085959Z", split.Original.RawPayload);
        Assert.Equal([Nov9.AddDays(-7), Nov9], Occurrences(split.Original).Select(o => o.Start));
        var following = split.Following!;
        Assert.NotEqual(item.Id, following.Id);
        Assert.Null(following.Etag);
        var uid = Calendar.Load(following.RawPayload)!.Events[0].Uid!;
        Assert.NotEqual("w1", uid);
        Assert.Equal($"https://dav.example.test/ben/home/{uid}.ics", following.RemoteId);
        Assert.Equal([nov16, nov16.AddDays(7), nov16.AddDays(14)], Occurrences(following).Select(o => o.Start));
        Assert.All(Occurrences(following), o => Assert.Equal("New standup", CalDavRecurrence.ReadOccurrence(following, o.Recurrence, London).Title));
        Assert.Equal(nov16, split.Recurrence);
    }

    [Fact]
    public void Split_ExceptionsGoToTheirSide()
    {
        var item = Existing(Weekly);
        item = CalDavRecurrence.DeleteOccurrence(item, Nov9, London, Now);
        item = CalDavRecurrence.DeleteOccurrence(item, Nov9.AddDays(21), London, Now);
        var nov23 = Nov9.AddDays(14);
        item = CalDavRecurrence.EditOccurrence(item, nov23, CalDavRecurrence.ReadOccurrence(item, nov23, London) with { Title = "Retro" }, London, Now)!;

        var split = Split(item, Nov9.AddDays(7), f => f with { Location = "Room 2" });

        var original = Calendar.Load(split.Original.RawPayload)!;
        Assert.Single(original.Events);
        Assert.Single(original.Events[0].ExceptionDates.GetAllDates());
        var following = Calendar.Load(split.Following!.RawPayload)!;
        Assert.Equal(2, following.Events.Count);
        Assert.All(following.Events, e => Assert.Equal(following.Events[0].Uid, e.Uid));
        Assert.Equal("Retro", CalDavRecurrence.ReadOccurrence(split.Following, nov23, London).Title);
        Assert.Equal([Nov9.AddDays(7), nov23], Occurrences(split.Following).Select(o => o.Start));
    }

    [Fact]
    public void Split_CountIsSharedOutCountingExcludedOccurrences()
    {
        var item = Existing(Weekly.Replace("BYDAY=MO", "BYDAY=MO;COUNT=5"));
        item = CalDavRecurrence.DeleteOccurrence(item, Nov9, London, Now);
        var nov23 = Nov9.AddDays(14);

        var split = Split(item, nov23, f => f with { Title = "Later" });

        Assert.DoesNotContain("COUNT", split.Original.RawPayload);
        Assert.Equal([Nov9.AddDays(-7), Nov9.AddDays(7)], Occurrences(split.Original).Select(o => o.Start));
        Assert.Contains("COUNT=2", split.Following!.RawPayload);
        Assert.Equal([nov23, nov23.AddDays(7)], Occurrences(split.Following).Select(o => o.Start));
    }

    [Fact]
    public void Split_FirstOccurrence_ActsAsAll()
    {
        var item = Existing(Weekly);

        var split = Split(item, Nov9.AddDays(-7), f => f with { Title = "Everyone" });

        Assert.Null(split.Following);
        Assert.Equal("Everyone", CalDavRecurrence.ReadOccurrence(split.Original, Nov9.AddDays(14), London).Title);
    }

    [Fact]
    public void Split_MoveToTuesday_ShiftsTheNewSeriesOnly()
    {
        var item = Existing(Weekly);
        var nov16 = Nov9.AddDays(7);

        var split = Split(item, nov16, f => f with { Start = Wall(11, 17, 9), End = Wall(11, 17, 10) });

        Assert.Contains("BYDAY=MO", split.Original.RawPayload);
        Assert.Contains("BYDAY=TU", split.Following!.RawPayload);
        Assert.Equal(nov16.AddDays(1), split.Recurrence);
        Assert.Equal(Wall(11, 17, 9), CalDavRecurrence.ReadOccurrence(split.Following, split.Recurrence, London).Start);
    }

    [Theory]
    [InlineData("DTSTART;VALUE=DATE:20261102\nDTEND;VALUE=DATE:20261103", "UNTIL=20261104", 0)]
    [InlineData("DTSTART:20261102T090000\nDURATION:PT1H", "UNTIL=20261105T085959", 9)]
    public void Split_UntilKeepsTheSeriesForm(string times, string until, int hour)
    {
        var item = Existing($"BEGIN:VEVENT\nUID:d1\nDTSTAMP:20260101T000000Z\n{times}\nRRULE:FREQ=DAILY;COUNT=6\nSUMMARY:Run\nEND:VEVENT\n");
        var rid = new DateTimeOffset(2026, 11, 5, hour, 0, 0, TimeSpan.Zero);

        var split = Split(item, rid, f => f with { Title = "Walk" });

        Assert.Contains($"RRULE:FREQ=DAILY;{until}\r\n", split.Original.RawPayload);
        Assert.Equal(3, Occurrences(split.Original).Count);
        Assert.Equal(3, Occurrences(split.Following!).Count);
    }

    [Fact]
    public void DeleteFollowing_EndsSeries_FirstGivesNull()
    {
        var item = Existing(Weekly);

        var ended = CalDavRecurrence.DeleteFollowing(item, Nov9.AddDays(7), London, Now)!;

        Assert.Equal(2, Occurrences(ended).Count);
        Assert.Null(CalDavRecurrence.DeleteFollowing(item, Nov9.AddDays(-7), London, Now));
    }

    [Fact]
    public void IsOrganisedMeeting_MatchesOrganiserWithAttendees()
    {
        var meeting = Existing(Weekly.Replace("SUMMARY:Standup", "SUMMARY:Standup\nORGANIZER:mailto:ben@example.test\nATTENDEE:mailto:amy@example.test"));

        Assert.True(CalDavRecurrence.IsOrganisedMeeting(meeting, "Ben@example.test"));
        Assert.False(CalDavRecurrence.IsOrganisedMeeting(meeting, "amy@example.test"));
        Assert.False(CalDavRecurrence.IsOrganisedMeeting(Existing(Weekly), "ben@example.test"));
    }

    private static string Monthly(string parts, int day) => $"""
        BEGIN:VEVENT
        UID:m1
        DTSTAMP:20260101T000000Z
        DTSTART;TZID=Europe/London:202611{day:00}T090000
        DTEND;TZID=Europe/London:202611{day:00}T100000
        RRULE:FREQ=MONTHLY;{parts}
        SUMMARY:Review
        END:VEVENT

        """;
}
