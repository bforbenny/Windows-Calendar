using System.Globalization;
using WindowsCalendar.Core.QuickAdd;

namespace WindowsCalendar.Core.Tests;

/// <summary>Date phrases (search-date-chips R1, R2) and quick-add ranges (quick-add R2, R7). "Now" is Wednesday 2026-11-11 10:00.</summary>
public sealed class DatePhraseTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 10, 0, 0);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now);
    private static readonly QuickAddTarget Work = new("w", "Work", "Fastmail", true, true, false);
    private static readonly QuickAddTarget Tasks = new("t1", "Tasks", "Microsoft", true, true, true);

    private static DateOnly Day(int month, int day) => new(2026, month, day);

    private static DatePhrase? Find(string text, bool dayFirst = true) => QuickAddParser.FindDatePhrase(text, Today, dayFirst);

    private static QuickAddResult Parse(string phrase, DayOfWeek weekStart = DayOfWeek.Monday) =>
        QuickAddParser.Parse(phrase, Now, new QuickAddOptions(true, [Work, Tasks], WeekStart: weekStart));

    private static (DatePhraseKind, DateOnly?) KindAndDate(DatePhrase? p) => (p!.Kind, p.Date);

    [Fact]
    public void FindDatePhrase_ReportsKinds()
    {
        Assert.Equal((DatePhraseKind.Day, (DateOnly?)Today), KindAndDate(Find("today")));
        Assert.Equal((DatePhraseKind.Day, (DateOnly?)Day(11, 12)), KindAndDate(Find("tomorrow")));
        Assert.Equal(DatePhraseKind.Weekday, Find("monday")!.Kind);
        Assert.Equal(DayOfWeek.Monday, Find("monday")!.Weekday);
        Assert.Equal((DatePhraseKind.Day, (DateOnly?)Day(11, 20)), KindAndDate(Find("next fri")));
        Assert.Equal((DatePhraseKind.Day, (DateOnly?)Day(12, 5)), KindAndDate(Find("5 dec")));
        Assert.Equal((DatePhraseKind.Day, (DateOnly?)Day(12, 5)), KindAndDate(Find("5/12")));
        Assert.Equal((DatePhraseKind.Day, (DateOnly?)new DateOnly(2027, 5, 12)), KindAndDate(Find("5/12", dayFirst: false)));
        foreach (var (text, kind, shift) in new[]
        {
            ("this week", DatePhraseKind.Week, 0), ("next week", DatePhraseKind.Week, 1), ("last week", DatePhraseKind.Week, -1),
            ("this month", DatePhraseKind.Month, 0), ("next month", DatePhraseKind.Month, 1), ("last month", DatePhraseKind.Month, -1),
        })
        {
            var phrase = Find(text)!;
            Assert.Equal(kind, phrase.Kind);
            Assert.Equal(shift, phrase.Shift);
        }
        Assert.Equal(DatePhraseKind.MonthName, Find("march")!.Kind);
        Assert.Equal(3, Find("march")!.Month);
    }

    [Fact]
    public void FindDatePhrase_CommonWordsNeedAnAnchor()
    {
        Assert.Equal(DayOfWeek.Sunday, Find("sun")!.Weekday);
        Assert.Null(Find("sun cream"));
        Assert.Null(Find("may the best"));
    }

    [Fact]
    public void FindDatePhrase_SpanCoversThePhraseOnly()
    {
        const string text = "budget next week";
        Assert.Equal("next week", text[Find(text)!.Span]);
        Assert.Null(Find("budget review"));
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, "2026-11-09", "2026-11-16", "2026-11-16", "2026-11-23", "2026-11-02", "2026-11-09")]
    [InlineData(DayOfWeek.Sunday, "2026-11-08", "2026-11-15", "2026-11-15", "2026-11-22", "2026-11-01", "2026-11-08")]
    public void Range_Weeks(DayOfWeek weekStart, string thisFrom, string thisTo, string nextFrom, string nextTo, string lastFrom, string lastTo)
    {
        Assert.Equal((DateOnly.Parse(thisFrom), DateOnly.Parse(thisTo)), SearchDates.Range(Find("this week")!, Today, weekStart));
        Assert.Equal((DateOnly.Parse(nextFrom), DateOnly.Parse(nextTo)), SearchDates.Range(Find("next week")!, Today, weekStart));
        Assert.Equal((DateOnly.Parse(lastFrom), DateOnly.Parse(lastTo)), SearchDates.Range(Find("last week")!, Today, weekStart));
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, 15)]
    [InlineData(DayOfWeek.Sunday, 8)]
    public void Range_WeekdayIsInTheCurrentWeek_EvenWhenPast(DayOfWeek weekStart, int sundayDay)
    {
        Assert.Equal((Day(11, 9), Day(11, 10)), SearchDates.Range(Find("monday")!, Today, weekStart));
        Assert.Equal((Day(11, sundayDay), Day(11, sundayDay + 1)), SearchDates.Range(Find("sunday")!, Today, weekStart));
    }

    [Fact]
    public void Range_MonthsDaysAndMonthNames()
    {
        Assert.Equal((Day(11, 1), Day(12, 1)), SearchDates.Range(Find("this month")!, Today, DayOfWeek.Monday));
        Assert.Equal((Day(12, 1), new DateOnly(2027, 1, 1)), SearchDates.Range(Find("next month")!, Today, DayOfWeek.Monday));
        Assert.Equal((Day(10, 1), Day(11, 1)), SearchDates.Range(Find("last month")!, Today, DayOfWeek.Monday));
        Assert.Equal((Day(3, 1), Day(4, 1)), SearchDates.Range(Find("march")!, Today, DayOfWeek.Monday));
        Assert.Equal((Day(11, 12), Day(11, 13)), SearchDates.Range(Find("tomorrow")!, Today, DayOfWeek.Monday));
        Assert.Equal((Day(12, 5), Day(12, 6)), SearchDates.Range(Find("5 dec")!, Today, DayOfWeek.Monday));
    }

    [Fact]
    public void Label_NamesThePeriod()
    {
        var culture = CultureInfo.GetCultureInfo("en-GB");
        string L(string text)
        {
            var p = Find(text)!;
            return SearchDates.Label(p, SearchDates.Range(p, Today, DayOfWeek.Monday), culture);
        }
        Assert.Equal("Next week", L("next week"));
        Assert.Equal("This month", L("this month"));
        Assert.Equal("Last week", L("last week"));
        Assert.Equal("Monday", L("monday"));
        Assert.Equal("March", L("march"));
        Assert.Contains("12", L("tomorrow"));
    }

    // Quick-add ranges (quick-add R2)

    [Fact]
    public void QuickAdd_NextWeekEventIsAllDayOnTheFirstDay()
    {
        var r = Parse("Dentist next week");
        Assert.Equal("Dentist", r.Title);
        Assert.True(r.AllDay);
        Assert.Equal(new DateTime(2026, 11, 16), r.Start);
        Assert.Equal(new DateTime(2026, 11, 15), Parse("Dentist next week", DayOfWeek.Sunday).Start);
        Assert.Equal("next week", "Dentist next week"[r.Spans[QuickAddField.Date]]);
    }

    [Fact]
    public void QuickAdd_ThisWeekAndThisMonthMeanToday()
    {
        Assert.Equal(new DateTime(2026, 11, 11), Parse("Dentist this week").Start);
        Assert.Equal(new DateTime(2026, 11, 11), Parse("Dentist this month").Start);
    }

    [Fact]
    public void QuickAdd_NextWeekWithATimeIsTimed()
    {
        var r = Parse("Dentist next week 3pm");
        Assert.False(r.AllDay);
        Assert.Equal(new DateTime(2026, 11, 16, 15, 0, 0), r.Start);
    }

    [Fact]
    public void QuickAdd_NextMonthIsTheFirst()
    {
        Assert.Equal(new DateTime(2026, 12, 1), Parse("Pay rent next month").Start);
    }

    [Fact]
    public void QuickAdd_TaskDueOnTheLastDayOfTheRange()
    {
        Assert.Equal(QuickAddKind.Task, Parse("task Report next week").Kind);
        Assert.Equal(new DateTime(2026, 11, 22), Parse("task Report next week").Start);
        Assert.Equal(new DateTime(2026, 11, 21), Parse("task Report next week", DayOfWeek.Sunday).Start);
        Assert.Equal(new DateTime(2026, 11, 30), Parse("task x this month").Start);
    }

    [Fact]
    public void QuickAdd_LastWeekStaysInTheTitle()
    {
        var r = Parse("Review last week");
        Assert.Equal("Review last week", r.Title);
        Assert.Equal(new DateTime(2026, 11, 11), r.Start);
        Assert.Equal("Review last month", Parse("Review last month").Title);
    }

    // R1 guard: the shared recogniser reports what Parse uses.

    [Theory]
    [InlineData("Lunch fri")]
    [InlineData("tomorrow dentist")]
    [InlineData("call today")]
    [InlineData("Lunch with Sam fri 1pm at Cafe Rio #Personal")]
    [InlineData("task submit report tomorrow")]
    [InlineData("lunch next fri")]
    [InlineData("next monday gym")]
    [InlineData("pay in 3 days")]
    [InlineData("pay in 2 weeks")]
    [InlineData("party 5 Dec")]
    [InlineData("party Dec 5")]
    [InlineData("party 5th december")]
    [InlineData("party 25/12")]
    [InlineData("party 25/12/2027")]
    [InlineData("party 1/2/27")]
    [InlineData("Dentist next week")]
    [InlineData("Dentist this week")]
    [InlineData("Pay rent next month")]
    [InlineData("Pay rent this month")]
    [InlineData("gym monday")]
    [InlineData("gym tue")]
    [InlineData("gym wednesday")]
    [InlineData("gym thurs")]
    [InlineData("see you sat")]
    [InlineData("pick up sun")]
    [InlineData("meet wed")]
    [InlineData("meet mar 5")]
    [InlineData("may 5 party")]
    [InlineData("budget review tomorrow at 3pm for 2h")]
    [InlineData("sun cream")]
    public void FindDatePhrase_AgreesWithParse(string text)
    {
        var parsed = QuickAddParser.Parse(text, Now, new QuickAddOptions(true, [Work, Tasks]));
        var phrase = QuickAddParser.FindDatePhrase(text, Today, dayFirst: true);
        if (!parsed.Spans.TryGetValue(QuickAddField.Date, out var span))
        {
            Assert.True(phrase is null || phrase.Kind == DatePhraseKind.MonthName);
            return;
        }
        Assert.NotNull(phrase);
        Assert.Equal(text[span], text[phrase.Span]);
        if (phrase.Kind == DatePhraseKind.Day)
        {
            Assert.Equal(DateOnly.FromDateTime(parsed.Start), phrase.Date);
        }
    }
}
