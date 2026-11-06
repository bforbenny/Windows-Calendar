using Ical.Net;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>Reading and changing repeat rules of CalDAV events (recurring-events R3 interim).</summary>
public sealed class CalDavRuleTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 1, 0, 0, TimeSpan.Zero);
    private static readonly Collection Home = new("c1", "a1", CollectionKind.Calendar, "Home", "Navy", "Circle", true, false, "https://dav.example.test/ben/home/");

    // Monday 2 Nov 2026 09:00–10:00 London (GMT, so wall time = UTC).
    private static string Event(string extra = "") => $"""
        BEGIN:VEVENT
        UID:w1
        DTSTAMP:20260101T000000Z
        DTSTART;TZID=Europe/London:20261102T090000
        DTEND;TZID=Europe/London:20261102T100000
        {extra}SUMMARY:Standup
        SEQUENCE:1
        END:VEVENT

        """;

    private static readonly string Weekly = Event("RRULE:FREQ=WEEKLY;BYDAY=MO\n");

    private static readonly DateTimeOffset Nov2 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static RepeatRule Rule(RepeatFrequency frequency, int interval = 1, DayOfWeek[]? days = null, int? week = null, int? count = null,
        DateOnly? until = null) => new(frequency, interval, days ?? [], week, count, until);

    private static Item Existing(string body) =>
        CalDavItemMapper.Map("https://dav.example.test/ben/home/x.ics", "\"e1\"",
            ("BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Test//EN\n" + body + "END:VCALENDAR\n").Replace("\n", "\r\n"), London)!
        with
        { Id = "i1", CollectionId = "c1" };

    private static List<DateTimeOffset> Starts(Item item) =>
        OccurrenceExpander.Expand(Calendar.Load(item.RawPayload)!, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero), London).Select(o => o.Start).ToList();

    private static RuleChange Change(Item item, DateTimeOffset? recurrence, bool following, RepeatRule rule) =>
        CalDavRecurrence.ChangeRule(item, recurrence, following, rule, Home, London, Now)!;

    [Fact]
    public void ReadRule_NoneForSingleEvent() =>
        Assert.Equal(RepeatRule.None, CalDavRecurrence.ReadRule(Existing(Event()), London));

    [Fact]
    public void ReadRule_Weekly() =>
        Assert.Equal(Rule(RepeatFrequency.Weekly, days: [DayOfWeek.Monday]), CalDavRecurrence.ReadRule(Existing(Weekly), London));

    [Fact]
    public void ReadRule_MonthlyNthWeekday() =>
        Assert.Equal(Rule(RepeatFrequency.Monthly, week: 1),
            CalDavRecurrence.ReadRule(Existing(Event("RRULE:FREQ=MONTHLY;BYDAY=1MO\n")), London));

    [Theory]
    [InlineData("RRULE:FREQ=MONTHLY;BYDAY=MO;BYSETPOS=1\n")]
    [InlineData("RRULE:FREQ=WEEKLY;BYDAY=TU\n")]
    [InlineData("RRULE:FREQ=MONTHLY;BYMONTHDAY=1,15\n")]
    [InlineData("RRULE:FREQ=WEEKLY\nRDATE;TZID=Europe/London:20261104T090000\n")]
    public void ReadRule_NullWhenControlsCantShowIt(string rule) =>
        Assert.Null(CalDavRecurrence.ReadRule(Existing(Event(rule)), London));

    [Fact]
    public void ReadRule_UntilBeforeTheStartTimeIsTheDayBefore()
    {
        // As a split writes it: the 16 Nov occurrence is excluded, so the last day is 15 Nov.
        var rule = CalDavRecurrence.ReadRule(Existing(Event("RRULE:FREQ=WEEKLY;BYDAY=MO;UNTIL=20261116T085959Z\n")), London)!;

        Assert.Equal(new DateOnly(2026, 11, 15), rule.Until);
    }

    [Fact]
    public void ChangeRule_UnchangedIsNull() =>
        Assert.Null(CalDavRecurrence.ChangeRule(Existing(Weekly), Nov2, false, Rule(RepeatFrequency.Weekly, days: [DayOfWeek.Monday]),
            Home, London, Now));

    [Fact]
    public void ChangeRule_MakesSingleEventRepeat()
    {
        var change = Change(Existing(Event()), null, false, Rule(RepeatFrequency.Daily, count: 3));

        Assert.Contains("RRULE:FREQ=DAILY;COUNT=3", change.Original.RawPayload);
        Assert.Equal([Nov2, Nov2.AddDays(1), Nov2.AddDays(2)], Starts(change.Original));
        Assert.Equal(Nov2, change.Recurrence);
        Assert.Null(change.Following);
    }

    [Fact]
    public void ChangeRule_WeeklyStartMovesToFirstChosenDay()
    {
        var change = Change(Existing(Event()), null, false, Rule(RepeatFrequency.Weekly, days: [DayOfWeek.Wednesday, DayOfWeek.Friday], count: 3));

        Assert.Equal([Nov2.AddDays(2), Nov2.AddDays(4), Nov2.AddDays(9)], Starts(change.Original));
        Assert.Equal(Nov2.AddDays(2), change.Recurrence);
    }

    [Fact]
    public void ChangeRule_MonthlyLastWeekday()
    {
        // Monday 30 Nov is the last Monday.
        var item = Existing(Event().Replace("20261102", "20261130"));

        var change = Change(item, null, false, Rule(RepeatFrequency.Monthly, week: -1, count: 2));

        Assert.Contains("BYDAY=-1MO", change.Original.RawPayload);
        Assert.Equal([Nov2.AddDays(28), new DateTimeOffset(2026, 12, 28, 9, 0, 0, TimeSpan.Zero)], Starts(change.Original));
    }

    [Fact]
    public void ChangeRule_UntilIsInclusiveInUtc()
    {
        var change = Change(Existing(Weekly), Nov2, false, Rule(RepeatFrequency.Weekly, days: [DayOfWeek.Monday], until: new DateOnly(2026, 11, 16)));

        Assert.Contains("UNTIL=20261116T235959Z", change.Original.RawPayload);
        Assert.Equal([Nov2, Nov2.AddDays(7), Nov2.AddDays(14)], Starts(change.Original));
        Assert.Equal(new DateOnly(2026, 11, 16), CalDavRecurrence.ReadRule(change.Original, London)!.Until);
    }

    [Fact]
    public void ChangeRule_AllDayUntilIsADate()
    {
        var item = Existing("""
            BEGIN:VEVENT
            UID:d1
            DTSTAMP:20260101T000000Z
            DTSTART;VALUE=DATE:20261102
            DTEND;VALUE=DATE:20261103
            SUMMARY:Holiday
            END:VEVENT

            """);

        var change = Change(item, null, false, Rule(RepeatFrequency.Daily, until: new DateOnly(2026, 11, 4)));

        Assert.Contains("UNTIL=20261104", change.Original.RawPayload);
        Assert.DoesNotContain("UNTIL=20261104T", change.Original.RawPayload);
        Assert.Equal(3, Starts(change.Original).Count);
    }

    [Fact]
    public void ChangeRule_AllDropsOverridesAndExdatesThatNoLongerFit()
    {
        // Overrides on 9 and 16 Nov and an EXDATE on 23 Nov; every 2 weeks keeps 16 Nov only.
        var item = Existing(Event("RRULE:FREQ=WEEKLY;BYDAY=MO\nEXDATE;TZID=Europe/London:20261123T090000\n") + """
            BEGIN:VEVENT
            UID:w1
            DTSTAMP:20260101T000000Z
            RECURRENCE-ID;TZID=Europe/London:20261109T090000
            DTSTART;TZID=Europe/London:20261109T110000
            DTEND;TZID=Europe/London:20261109T120000
            SUMMARY:Moved
            END:VEVENT
            BEGIN:VEVENT
            UID:w1
            DTSTAMP:20260101T000000Z
            RECURRENCE-ID;TZID=Europe/London:20261116T090000
            DTSTART;TZID=Europe/London:20261116T090000
            DTEND;TZID=Europe/London:20261116T100000
            SUMMARY:Kept
            END:VEVENT

            """);

        var change = Change(item, Nov2.AddDays(7), false, Rule(RepeatFrequency.Weekly, interval: 2, days: [DayOfWeek.Monday]));

        Assert.Equal(1, change.Lost);
        var calendar = Calendar.Load(change.Original.RawPayload)!;
        Assert.Equal("Kept", calendar.Events.Single(e => e.RecurrenceIdentifier is not null).Summary);
        Assert.DoesNotContain("EXDATE", change.Original.RawPayload);
        // 9 Nov is gone, so the editor goes to the series' start.
        Assert.Equal(Nov2, change.Recurrence);
    }

    [Fact]
    public void ChangeRule_FollowingSplitsAndGivesNewSeriesTheRule()
    {
        var nov16 = Nov2.AddDays(14);

        var change = Change(Existing(Weekly), nov16, true, Rule(RepeatFrequency.Daily, count: 2));

        Assert.Equal([Nov2, Nov2.AddDays(7)], Starts(change.Original));
        var following = change.Following!;
        Assert.NotEqual("w1", Calendar.Load(following.RawPayload)!.Events[0].Uid);
        Assert.Equal([nov16, nov16.AddDays(1)], Starts(following));
        Assert.Equal(nov16, change.Recurrence);
    }

    [Fact]
    public void ChangeRule_FollowingKeepsCountingAnUnchangedCount()
    {
        var item = Existing(Event("RRULE:FREQ=WEEKLY;BYDAY=MO;COUNT=4\n"));

        // Every 2 weeks from 16 Nov, with the 2 remaining occurrences.
        var change = Change(item, Nov2.AddDays(14), true, Rule(RepeatFrequency.Weekly, interval: 2, days: [DayOfWeek.Monday], count: 4));

        Assert.Contains("COUNT=2", change.Following!.RawPayload);
        Assert.Equal([Nov2.AddDays(14), Nov2.AddDays(28)], Starts(change.Following));
    }

    [Fact]
    public void ChangeRule_FollowingOnFirstOccurrenceActsAsAll()
    {
        var change = Change(Existing(Weekly), Nov2, true, Rule(RepeatFrequency.Daily, count: 2));

        Assert.Null(change.Following);
        Assert.Equal([Nov2, Nov2.AddDays(1)], Starts(change.Original));
    }

    [Fact]
    public void ChangeRule_NoneMakesSeriesOneEventAtItsStart()
    {
        var item = Existing(Weekly + """
            BEGIN:VEVENT
            UID:w1
            DTSTAMP:20260101T000000Z
            RECURRENCE-ID;TZID=Europe/London:20261102T090000
            DTSTART;TZID=Europe/London:20261102T110000
            DTEND;TZID=Europe/London:20261102T120000
            SUMMARY:First, moved
            END:VEVENT
            BEGIN:VEVENT
            UID:w1
            DTSTAMP:20260101T000000Z
            RECURRENCE-ID;TZID=Europe/London:20261109T090000
            DTSTART;TZID=Europe/London:20261109T110000
            DTEND;TZID=Europe/London:20261109T120000
            SUMMARY:Second, moved
            END:VEVENT

            """);

        var change = Change(item, Nov2.AddDays(7), false, RepeatRule.None);

        Assert.Equal(1, change.Lost);
        Assert.Null(change.Recurrence);
        var ev = Calendar.Load(change.Original.RawPayload)!.Events.Single();
        Assert.Equal("First, moved", ev.Summary);
        Assert.Null(ev.RecurrenceIdentifier);
        Assert.Null(ev.RecurrenceRule);
        Assert.Equal([Nov2.AddHours(2)], Starts(change.Original));
    }

    [Fact]
    public void ChangeRule_NoneFollowingEndsSeriesAndKeepsThisOccurrence()
    {
        var nov16 = Nov2.AddDays(14);

        var change = Change(Existing(Weekly), nov16, true, RepeatRule.None);

        Assert.Equal([Nov2, Nov2.AddDays(7)], Starts(change.Original));
        Assert.Equal([nov16], Starts(change.Following!));
        Assert.Null(change.Recurrence);
    }

    [Fact]
    public void ChangeRule_RefusesRuleTheControlsCantShow() =>
        Assert.Throws<EditRefusedException>(() => CalDavRecurrence.ChangeRule(Existing(Event("RRULE:FREQ=MONTHLY;BYDAY=MO;BYSETPOS=1\n")),
            Nov2, false, Rule(RepeatFrequency.Daily), Home, London, Now));
}
