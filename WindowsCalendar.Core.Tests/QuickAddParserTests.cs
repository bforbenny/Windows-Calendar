using System.Globalization;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.QuickAdd;

namespace WindowsCalendar.Core.Tests;

/// <summary>The quick-add parser (quick-add R2, R3, R5-R7). "Now" is Wednesday 2026-11-11 10:00.</summary>
public sealed class QuickAddParserTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 10, 0, 0);

    private static readonly QuickAddTarget Work = new("w", "Work", "Fastmail", true, true, false);
    private static readonly QuickAddTarget Personal = new("p", "Personal", "Fastmail", true, false, false);
    private static readonly QuickAddTarget Wellness = new("wl", "Wellness", "Fastmail", true, false, false);
    private static readonly QuickAddTarget CafeRio = new("cr", "Cafe Rio", "Fastmail", true, false, false);
    private static readonly QuickAddTarget Holidays = new("h", "Holidays", "Outlook", false, false, false);
    private static readonly QuickAddTarget Birthdays = new("b", "Birthdays", "Outlook", true, false, false);
    private static readonly QuickAddTarget Tasks = new("t1", "Tasks", "Microsoft", true, true, true);
    private static readonly QuickAddTarget Shopping = new("t2", "Shopping", "Microsoft", true, false, true);
    private static readonly QuickAddTarget Archive = new("t3", "Archive", "Microsoft", false, false, true);

    private static readonly QuickAddTarget[] All = [Work, Personal, Wellness, CafeRio, Holidays, Birthdays, Tasks, Shopping, Archive];

    private static DateTime D(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture);

    private static QuickAddResult Parse(string phrase, bool dayFirst = true, DateTime? now = null, DateTime? slot = null,
        bool slotAllDay = false, bool taskByDefault = false, QuickAddTarget[]? targets = null) =>
        QuickAddParser.Parse(phrase, now ?? Now, new QuickAddOptions(dayFirst, targets ?? All, slot, slotAllDay, taskByDefault));

    private static string Text(string phrase, QuickAddResult r, QuickAddField field) => phrase[r.Spans[field]];

    // Acceptance criteria

    [Fact]
    public void Lunch_with_Sam_acceptance_phrase()
    {
        const string phrase = "Lunch with Sam fri 1pm at Cafe Rio #Personal";
        var r = Parse(phrase);
        Assert.Equal(QuickAddKind.Event, r.Kind);
        Assert.Equal("Lunch with Sam", r.Title);
        Assert.Equal(D("2026-11-13 13:00"), r.Start);
        Assert.Equal(D("2026-11-13 14:00"), r.End);
        Assert.Equal("Cafe Rio", r.Location);
        Assert.Equal(Personal, r.Target);
        Assert.Null(r.Error);
        Assert.Equal("fri", Text(phrase, r, QuickAddField.Date));
        Assert.Equal("1pm", Text(phrase, r, QuickAddField.Time));
        Assert.Equal("at Cafe Rio", Text(phrase, r, QuickAddField.Location));
        Assert.Equal("#Personal", Text(phrase, r, QuickAddField.Target));
    }

    [Fact]
    public void Task_submit_report_tomorrow_acceptance_phrase()
    {
        const string phrase = "task submit report tomorrow";
        var r = Parse(phrase);
        Assert.Equal(QuickAddKind.Task, r.Kind);
        Assert.Equal("submit report", r.Title);
        Assert.Equal(new DateTime(2026, 11, 12), r.Start.Date);
        Assert.Equal(Tasks, r.Target);
        Assert.Equal("task", Text(phrase, r, QuickAddField.Kind));
        Assert.Equal("tomorrow", Text(phrase, r, QuickAddField.Date));
    }

    [Fact]
    public void Standup_weekdays_acceptance_phrase()
    {
        const string phrase = "Standup weekdays 9:15 for 15m";
        var r = Parse(phrase);
        Assert.Equal("Standup", r.Title);
        Assert.Equal(D("2026-11-11 09:15"), r.Start);
        Assert.Equal(D("2026-11-11 09:30"), r.End);
        Assert.Equal(RepeatFrequency.Weekly, r.Repeat.Frequency);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], r.Repeat.Weekdays);
        Assert.Equal("for 15m", Text(phrase, r, QuickAddField.Duration));
        Assert.Equal("weekdays", Text(phrase, r, QuickAddField.Repeat));
    }

    [Fact]
    public void Look_at_budget_tomorrow_has_no_location()
    {
        var r = Parse("look at budget tomorrow");
        Assert.Equal("look at budget", r.Title);
        Assert.Null(r.Location);
        Assert.Equal(new DateTime(2026, 11, 12), r.Start);
    }

    // R2 dates

    [Theory]
    [InlineData("Meet today", "2026-11-11")]
    [InlineData("Meet TOMORROW", "2026-11-12")]
    [InlineData("Meet thu", "2026-11-12")]
    [InlineData("Meet Friday", "2026-11-13")]
    [InlineData("Meet mon", "2026-11-16")]
    [InlineData("Meet in 3 days", "2026-11-14")]
    [InlineData("Meet in 1 day", "2026-11-12")]
    [InlineData("Meet in 2 weeks", "2026-11-25")]
    [InlineData("Meet 5 Dec", "2026-12-05")]
    [InlineData("Meet Dec 5", "2026-12-05")]
    [InlineData("Meet 5th December", "2026-12-05")]
    [InlineData("Meet 11 Nov", "2026-11-11")]
    public void Dates_are_all_day_with_no_time(string phrase, string date)
    {
        var r = Parse(phrase);
        Assert.Equal("Meet", r.Title);
        Assert.True(r.AllDay);
        Assert.Equal(D(date), r.Start);
        Assert.Equal(D(date).AddDays(1), r.End);
    }

    [Theory]
    [InlineData(true, "2026-12-05")]
    [InlineData(false, "2027-05-12")]
    public void Numeric_dates_follow_the_short_date_order(bool dayFirst, string date)
    {
        var r = Parse("Meet 5/12", dayFirst);
        Assert.Equal("Meet", r.Title);
        Assert.Equal(D(date), r.Start);
    }

    [Theory]
    [InlineData("Meet 13/12", true, "2026-12-13")]
    [InlineData("Meet 12/13", false, "2026-12-13")]
    [InlineData("Meet 5/12/2027", true, "2027-12-05")]
    [InlineData("Meet 5/12/27", false, "2027-05-12")]
    public void Numeric_dates_with_year_or_large_day(string phrase, bool dayFirst, string date) =>
        Assert.Equal(D(date), Parse(phrase, dayFirst).Start);

    [Fact]
    public void Numeric_date_that_is_not_a_date_stays_in_the_title()
    {
        var r = Parse("Room 13/13");
        Assert.Equal("Room 13/13", r.Title);
        Assert.False(r.Spans.ContainsKey(QuickAddField.Date));
    }

    // R6 dates

    [Fact]
    public void Weekday_is_today_when_today_is_that_day() => Assert.Equal(new DateTime(2026, 11, 11), Parse("Meet wed").Start);

    [Theory]
    [InlineData("Meet next fri", "2026-11-20")]
    [InlineData("Meet next wed", "2026-11-18")]
    [InlineData("Meet next sun", "2026-11-22")]
    public void Next_weekday_is_the_one_after_the_next_occurrence(string phrase, string date)
    {
        var r = Parse(phrase);
        Assert.Equal("Meet", r.Title);
        Assert.Equal(D(date), r.Start);
    }

    [Theory]
    [InlineData("Meet 5 Nov", "2027-11-05")]
    [InlineData("Meet Nov 10", "2027-11-10")]
    [InlineData("Meet 10 Nov", "2027-11-10")]
    [InlineData("Meet 5 Oct", "2027-10-05")]
    [InlineData("Meet 29 Feb", "2028-02-29")]
    public void A_day_month_already_past_means_next_year(string phrase, string date)
    {
        // 10 Nov is past (now is 11 Nov); 5 Nov is past; 29 Feb waits for the next leap year.
        Assert.Equal(D(date), Parse(phrase).Start);
    }

    [Fact]
    public void Past_numeric_date_means_next_year_for_both_orders()
    {
        Assert.Equal(D("2027-03-02"), Parse("Meet 2/3", true).Start);
        Assert.Equal(D("2027-02-03"), Parse("Meet 2/3", false).Start);
    }

    [Theory]
    [InlineData("Sun cream")]
    [InlineData("May's party")]
    [InlineData("Sat nav update")]
    [InlineData("Wed Mar May")]
    public void Common_words_stay_in_the_title(string phrase)
    {
        var r = Parse(phrase);
        Assert.Equal(phrase, r.Title);
        Assert.False(r.Spans.ContainsKey(QuickAddField.Date));
    }

    [Fact]
    public void Common_word_weekday_alone_is_not_next_to_a_date_unless_a_real_date_follows()
    {
        var r = Parse("Sun cream fri");
        Assert.Equal("Sun cream", r.Title);
        Assert.Equal(new DateTime(2026, 11, 13), r.Start);
    }

    [Theory]
    [InlineData("lunch sun", "lunch", "2026-11-15")]
    [InlineData("lunch sat", "lunch", "2026-11-14")]
    [InlineData("lunch sun #Work", "lunch", "2026-11-15")]
    [InlineData("cream sun 7pm", "cream", "2026-11-15")]
    [InlineData("lunch 5 may", "lunch", "2027-05-05")]
    [InlineData("lunch may 5", "lunch", "2027-05-05")]
    [InlineData("lunch 5 mar", "lunch", "2027-03-05")]
    public void Common_words_count_as_dates_when_last_or_next_to_a_token(string phrase, string title, string date)
    {
        var r = Parse(phrase);
        Assert.Equal(title, r.Title);
        Assert.Equal(D(date).Date, r.Start.Date);
    }

    // R2 times

    [Theory]
    [InlineData("Call 1pm", "2026-11-11 13:00", "2026-11-11 14:00")]
    [InlineData("Call 1:30pm", "2026-11-11 13:30", "2026-11-11 14:30")]
    [InlineData("Call 13:00", "2026-11-11 13:00", "2026-11-11 14:00")]
    [InlineData("Call 9:15", "2026-11-11 09:15", "2026-11-11 10:15")]
    [InlineData("Call 12am", "2026-11-11 00:00", "2026-11-11 01:00")]
    [InlineData("Call 12pm", "2026-11-11 12:00", "2026-11-11 13:00")]
    [InlineData("Call noon", "2026-11-11 12:00", "2026-11-11 13:00")]
    [InlineData("Call midnight", "2026-11-11 00:00", "2026-11-11 01:00")]
    [InlineData("Call 9am", "2026-11-11 09:00", "2026-11-11 10:00")]
    [InlineData("Call fri 1PM", "2026-11-13 13:00", "2026-11-13 14:00")]
    public void Single_times_default_to_an_hour_today(string phrase, string start, string end)
    {
        var r = Parse(phrase);
        Assert.Equal("Call", r.Title);
        Assert.False(r.AllDay);
        Assert.Equal(D(start), r.Start);
        Assert.Equal(D(end), r.End);
    }

    [Theory]
    [InlineData("Call 1-2pm", "2026-11-11 13:00", "2026-11-11 14:00")]
    [InlineData("Call 1pm to 3pm", "2026-11-11 13:00", "2026-11-11 15:00")]
    [InlineData("Call 1pm-3pm", "2026-11-11 13:00", "2026-11-11 15:00")]
    [InlineData("Call 9:00-10:30", "2026-11-11 09:00", "2026-11-11 10:30")]
    [InlineData("Call 11am-1pm", "2026-11-11 11:00", "2026-11-11 13:00")]
    [InlineData("Call 11-1pm", "2026-11-11 11:00", "2026-11-11 13:00")]
    [InlineData("Call 9am to noon", "2026-11-11 09:00", "2026-11-11 12:00")]
    [InlineData("Call 9-5", "2026-11-11 09:00", "2026-11-11 17:00")]
    [InlineData("Call 7-9", "2026-11-11 07:00", "2026-11-11 09:00")]
    [InlineData("Call 10-11", "2026-11-11 10:00", "2026-11-11 11:00")]
    [InlineData("Call 11-1", "2026-11-11 11:00", "2026-11-11 13:00")]
    [InlineData("Call 9-12", "2026-11-11 09:00", "2026-11-11 12:00")]
    [InlineData("Call 2-4", "2026-11-11 14:00", "2026-11-11 16:00")]
    [InlineData("Call 9 to 5", "2026-11-11 09:00", "2026-11-11 17:00")]
    [InlineData("Call 11pm-1am", "2026-11-11 23:00", "2026-11-12 01:00")]
    [InlineData("Call 11pm to midnight", "2026-11-11 23:00", "2026-11-12 00:00")]
    public void Time_ranges(string phrase, string start, string end)
    {
        var r = Parse(phrase);
        Assert.Equal("Call", r.Title);
        Assert.False(r.AllDay);
        Assert.Equal(D(start), r.Start);
        Assert.Equal(D(end), r.End);
    }

    [Fact]
    public void A_time_that_has_passed_today_stays_today() => Assert.Equal(D("2026-11-11 08:00"), Parse("Gym 8am").Start);

    [Fact]
    public void A_range_wins_over_a_duration_and_the_duration_words_stay_in_the_title()
    {
        const string phrase = "Meet 1-2pm for 30m";
        var r = Parse(phrase);
        Assert.Equal("Meet for 30m", r.Title);
        Assert.Equal(D("2026-11-11 13:00"), r.Start);
        Assert.Equal(D("2026-11-11 14:00"), r.End);
        Assert.False(r.Spans.ContainsKey(QuickAddField.Duration));
    }

    // R6 bare numbers

    [Theory]
    [InlineData("Meet at 3pm", "2026-11-11 15:00")]
    [InlineData("Meet at 3", "2026-11-11 15:00")]
    [InlineData("Meet at 6", "2026-11-11 18:00")]
    [InlineData("Meet at 7", "2026-11-11 07:00")]
    [InlineData("Meet at 11", "2026-11-11 11:00")]
    [InlineData("Meet at 12", "2026-11-11 12:00")]
    [InlineData("Meet at 3 tomorrow", "2026-11-12 15:00")]
    [InlineData("Meet at 3 for 30m", "2026-11-11 15:00")]
    public void At_a_time_is_a_time_and_not_a_location(string phrase, string start)
    {
        var r = Parse(phrase);
        Assert.Equal("Meet", r.Title);
        Assert.Null(r.Location);
        Assert.Equal(D(start), r.Start);
    }

    [Fact]
    public void At_a_time_spans_include_the_at() => Assert.Equal("at 3pm", Text("Meet at 3pm", Parse("Meet at 3pm"), QuickAddField.Time));

    [Fact]
    public void A_bare_number_elsewhere_stays_in_the_title()
    {
        var r = Parse("Meet 3");
        Assert.Equal("Meet 3", r.Title);
        Assert.True(r.AllDay);
        Assert.False(r.Spans.ContainsKey(QuickAddField.Time));
    }

    [Fact]
    public void At_a_number_then_a_word_is_a_location()
    {
        var r = Parse("Meet at 12 Main St");
        Assert.Equal("Meet", r.Title);
        Assert.Equal("12 Main St", r.Location);
        Assert.True(r.AllDay);
    }

    // R2 duration

    [Theory]
    [InlineData("Chat 2pm for 30m", "2026-11-11 14:30")]
    [InlineData("Chat 2pm for 1h", "2026-11-11 15:00")]
    [InlineData("Chat 2pm for 90 min", "2026-11-11 15:30")]
    [InlineData("Chat 2pm for 2 hours", "2026-11-11 16:00")]
    [InlineData("Chat 2pm for 45 mins", "2026-11-11 14:45")]
    [InlineData("Chat 2pm for 1 hour", "2026-11-11 15:00")]
    public void Durations(string phrase, string end)
    {
        var r = Parse(phrase);
        Assert.Equal("Chat", r.Title);
        Assert.Equal(D("2026-11-11 14:00"), r.Start);
        Assert.Equal(D(end), r.End);
        Assert.True(r.Spans.ContainsKey(QuickAddField.Duration));
    }

    [Fact]
    public void For_without_a_duration_stays_in_the_title() => Assert.Equal("Gift for Sam", Parse("Gift for Sam 2pm").Title);

    // R2 all-day

    [Fact]
    public void All_day_phrase()
    {
        const string phrase = "Holiday all day fri";
        var r = Parse(phrase);
        Assert.Equal("Holiday", r.Title);
        Assert.True(r.AllDay);
        Assert.Equal(D("2026-11-13"), r.Start);
        Assert.Equal(D("2026-11-14"), r.End);
        Assert.Equal("all day", Text(phrase, r, QuickAddField.AllDay));
    }

    // R2 location

    [Theory]
    [InlineData("Party @ Joe's place", "Party", "Joe's place")]
    [InlineData("Drinks @ The Pub fri 7pm", "Drinks", "The Pub")]
    [InlineData("Meet at Cafe Rio", "Meet", "Cafe Rio")]
    [InlineData("Meet at Cafe Rio fri", "Meet", "Cafe Rio")]
    [InlineData("Meet fri at Cafe Rio, 7pm", "Meet", "Cafe Rio")]
    [InlineData("Meet at 12 Main St", "Meet", "12 Main St")]
    public void Locations(string phrase, string title, string location)
    {
        var r = Parse(phrase);
        Assert.Equal(title, r.Title);
        Assert.Equal(location, r.Location);
    }

    [Theory]
    [InlineData("Meet at the cafe")]
    [InlineData("Meet at")]
    [InlineData("Meet @")]
    public void No_location_when_the_next_word_does_not_qualify(string phrase)
    {
        var r = Parse(phrase);
        Assert.Equal(phrase, r.Title);
        Assert.Null(r.Location);
    }

    // R2 calendar

    [Fact]
    public void No_hash_uses_the_default_calendar() => Assert.Equal(Work, Parse("Lunch").Target);

    [Fact]
    public void No_hash_with_default_unticked_uses_the_first_ticked_calendar()
    {
        var r = Parse("Lunch", targets: [Work with { Ticked = false }, Personal, Holidays]);
        Assert.Equal(Personal, r.Target);
        Assert.False(r.TargetHidden);
    }

    [Theory]
    [InlineData("Lunch #pers")]
    [InlineData("Lunch #PERSONAL")]
    [InlineData("Lunch #Personal")]
    public void Hash_name_is_a_case_insensitive_prefix_match(string phrase) => Assert.Equal(Personal, Parse(phrase).Target);

    [Fact]
    public void Hash_name_tries_the_default_first_then_sidebar_order()
    {
        Assert.Equal(Work, Parse("Lunch #w").Target);
        Assert.Equal(Wellness, Parse("Lunch #we").Target);
        Assert.Equal(Wellness, Parse("Lunch #w", targets: [Wellness, Personal, Work with { IsDefault = false }]).Target);
    }

    [Fact]
    public void Hash_name_ignores_spaces_and_case()
    {
        Assert.Equal(CafeRio, Parse("Lunch #CafeRio").Target);
        Assert.Equal(CafeRio, Parse("Lunch #caf").Target);
        Assert.Equal(CafeRio, Parse("Lunch #fastmail/caferio").Target);
    }

    [Fact]
    public void Hash_name_does_not_reach_unticked_calendars()
    {
        var r = Parse("Lunch #Hol");
        Assert.Null(r.Target);
        Assert.Equal("No calendar matches #Hol", r.Error);
        Assert.Equal("Lunch", r.Title);
    }

    [Fact]
    public void Hash_account_name_picks_a_calendar_in_that_account()
    {
        var r = Parse("Lunch #fast/pers");
        Assert.Equal(Personal, r.Target);
        Assert.False(r.TargetHidden);
    }

    [Fact]
    public void Hash_account_name_can_reach_an_unticked_calendar_and_flags_it_hidden()
    {
        var r = Parse("Lunch #Outlook/Hol");
        Assert.Equal(Holidays, r.Target);
        Assert.True(r.TargetHidden);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Hash_account_name_tries_ticked_calendars_first()
    {
        var r = Parse("Lunch #Outlook/");
        Assert.Equal(Birthdays, r.Target);
        Assert.False(r.TargetHidden);
    }

    [Fact]
    public void Hash_account_name_with_no_match_is_an_error()
    {
        var r = Parse("Lunch #Nope/x");
        Assert.Null(r.Target);
        Assert.Equal("No calendar matches #Nope/x", r.Error);
    }

    [Fact]
    public void A_lone_hash_stays_in_the_title() => Assert.Equal("Issue # five", Parse("Issue # five").Title);

    [Fact]
    public void No_ticked_calendar_uses_the_default_calendar_and_flags_it_hidden()
    {
        var r = Parse("Lunch", targets: [Personal with { Ticked = false }, Work with { Ticked = false }]);
        Assert.Equal("Work", r.Target?.Name);
        Assert.True(r.TargetHidden);
        Assert.Null(r.Error);
    }

    [Fact]
    public void No_ticked_calendar_and_no_default_uses_the_first_calendar()
    {
        var r = Parse("Lunch", targets: [Holidays, Personal with { Ticked = false }]);
        Assert.Equal(Holidays, r.Target);
        Assert.True(r.TargetHidden);
        Assert.Null(r.Error);
    }

    [Fact]
    public void No_calendars_at_all_is_an_error()
    {
        var r = Parse("Lunch", targets: [Tasks]);
        Assert.Null(r.Target);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void An_exact_name_beats_an_earlier_prefix_match()
    {
        var workshop = new QuickAddTarget("ws", "Workshop", "Fastmail", true, false, false);
        var work = new QuickAddTarget("w2", "Work", "Fastmail", true, false, false);
        Assert.Equal(work, Parse("x #Work", targets: [workshop, work]).Target);
        Assert.Equal(work, Parse("x #Fastmail/Work", targets: [workshop, work]).Target);
        Assert.Equal(workshop, Parse("x #Works", targets: [workshop, work]).Target);
        Assert.Equal(work, Parse("x #wo", targets: [work, workshop]).Target);
    }

    [Fact]
    public void An_exact_account_beats_an_account_that_it_is_a_prefix_of()
    {
        var pro = new QuickAddTarget("a", "Home", "Fastmail Pro", true, false, false);
        var plain = new QuickAddTarget("b", "Home", "Fastmail", true, false, false);
        Assert.Equal(plain, Parse("x #Fastmail/Home", targets: [pro, plain]).Target);
        Assert.Equal(pro, Parse("x #FastmailPro/Home", targets: [pro, plain]).Target);
    }

    [Fact]
    public void Exact_match_is_tried_within_each_tier_so_a_ticked_prefix_still_beats_an_unticked_exact()
    {
        var workshop = new QuickAddTarget("ws", "Workshop", "Fastmail", true, false, false);
        var work = new QuickAddTarget("w2", "Work", "Fastmail", false, false, false);
        Assert.Equal(workshop, Parse("x #Fastmail/Work", targets: [workshop, work]).Target);
    }

    // R2 recurrence

    [Fact]
    public void Every_day()
    {
        var r = Parse("Pills every day 8am");
        Assert.Equal("Pills", r.Title);
        Assert.Equal(RepeatFrequency.Daily, r.Repeat.Frequency);
        Assert.Equal(1, r.Repeat.Interval);
        Assert.Equal("every day", Text("Pills every day 8am", r, QuickAddField.Repeat));
    }

    [Fact]
    public void Every_weekday_name_is_weekly_on_that_day_starting_on_its_next_occurrence()
    {
        var r = Parse("Gym every fri 6pm");
        Assert.Equal("Gym", r.Title);
        Assert.Equal(RepeatFrequency.Weekly, r.Repeat.Frequency);
        Assert.Equal([DayOfWeek.Friday], r.Repeat.Weekdays);
        Assert.Equal(D("2026-11-13 18:00"), r.Start);
    }

    [Fact]
    public void Every_weekday_name_does_not_override_a_typed_date()
    {
        var r = Parse("Gym every fri from 20 Nov");
        Assert.Equal(D("2026-11-20"), r.Start.Date);
        Assert.Equal([DayOfWeek.Friday], r.Repeat.Weekdays);
    }

    [Fact]
    public void Every_week_repeats_on_the_start_dates_weekday()
    {
        var r = Parse("Review every week");
        Assert.Equal(RepeatFrequency.Weekly, r.Repeat.Frequency);
        Assert.Equal([DayOfWeek.Wednesday], r.Repeat.Weekdays);
        var onFriday = Parse("Review every week fri");
        Assert.Equal([DayOfWeek.Friday], onFriday.Repeat.Weekdays);
    }

    [Fact]
    public void Every_month_repeats_on_the_same_day_of_the_month()
    {
        var r = Parse("Rent every month 1 Dec");
        Assert.Equal("Rent", r.Title);
        Assert.Equal(RepeatFrequency.Monthly, r.Repeat.Frequency);
        Assert.Null(r.Repeat.MonthlyWeek);
        Assert.Equal(D("2026-12-01"), r.Start);
    }

    [Fact]
    public void Weekdays_starting_on_a_weekend_begins_on_monday() =>
        Assert.Equal(D("2026-11-16 09:00"), Parse("Standup weekdays 9am", now: D("2026-11-14 10:00")).Start);

    [Fact]
    public void No_repeat_is_none() => Assert.Equal(RepeatRule.None, Parse("Lunch fri").Repeat);

    // Tasks

    [Theory]
    [InlineData("task buy milk")]
    [InlineData("TODO buy milk")]
    public void Task_prefix_creates_a_task_in_the_default_list(string phrase)
    {
        var r = Parse(phrase);
        Assert.Equal(QuickAddKind.Task, r.Kind);
        Assert.Equal("buy milk", r.Title);
        Assert.Equal(Tasks, r.Target);
        Assert.Equal(new DateTime(2026, 11, 11), r.Start.Date);
    }

    [Fact]
    public void Task_hash_matches_task_lists_only()
    {
        Assert.Equal(Shopping, Parse("task milk #Shop").Target);
        var r = Parse("task milk #Pers");
        Assert.Null(r.Target);
        Assert.Equal("No task list matches #Pers", r.Error);
        Assert.Equal(Personal, Parse("milk #Pers").Target);
    }

    [Fact]
    public void Task_to_an_unticked_list_via_account_is_hidden()
    {
        var r = Parse("task old #Microsoft/Arch");
        Assert.Equal(Archive, r.Target);
        Assert.True(r.TargetHidden);
        Assert.Equal(Archive, Parse("task old", targets: [Archive]).Target);
    }

    [Fact]
    public void Task_default_list_is_the_first_ticked_list()
    {
        var r = Parse("task x", targets: [Archive, Shopping with { IsDefault = false }, Tasks with { IsDefault = false }]);
        Assert.Equal(Shopping, r.Target);
    }

    [Fact]
    public void Task_takes_only_the_date_list_and_repeat_from_the_phrase()
    {
        var r = Parse("task call Bob 3pm every day");
        Assert.Equal(QuickAddKind.Task, r.Kind);
        Assert.Equal("call Bob 3pm", r.Title);
        Assert.Equal(new DateTime(2026, 11, 11), r.Start.Date);
        Assert.Equal(new RepeatRule(RepeatFrequency.Daily, 1, [], null, null, null), r.Repeat);
        Assert.Null(r.Location);

        var more = Parse("task call mum fri 1-2pm for 30m at Home all day #Shop");
        Assert.Equal("call mum 1-2pm for 30m at Home all day", more.Title);
        Assert.Equal(new DateTime(2026, 11, 13), more.Start.Date);
        Assert.Equal(Shopping, more.Target);
        Assert.Equal([QuickAddField.Kind, QuickAddField.Date, QuickAddField.Target], more.Spans.Keys.Order());
    }

    [Fact]
    public void Task_every_weekday_name_is_due_on_the_next_one_and_repeats_weekly()
    {
        var r = Parse("task x every Fri");
        Assert.Equal(QuickAddKind.Task, r.Kind);
        Assert.Equal("x", r.Title);
        Assert.Equal(new DateTime(2026, 11, 13), r.Start.Date);
        Assert.Equal(new RepeatRule(RepeatFrequency.Weekly, 1, [DayOfWeek.Friday], null, null, null), r.Repeat);
        Assert.Contains("Every Friday", QuickAddParser.Chips(r));
    }

    [Fact]
    public void Task_every_day_with_a_date_starts_then_and_leaves_the_time_in_the_title()
    {
        var r = Parse("task x every day tomorrow 3pm");
        Assert.Equal("x 3pm", r.Title);
        Assert.Equal(new DateTime(2026, 11, 12), r.Start.Date);
        Assert.Equal(new RepeatRule(RepeatFrequency.Daily, 1, [], null, null, null), r.Repeat);
    }

    [Fact]
    public void Task_weekdays_starts_on_the_first_weekday()
    {
        var r = Parse("task x weekdays sat");
        Assert.Equal(new DateTime(2026, 11, 16), r.Start.Date);
        Assert.Equal(RepeatFrequency.Weekly, r.Repeat.Frequency);
    }

    [Fact]
    public void Task_default_list_is_the_default_list_even_when_unticked()
    {
        var r = Parse("task x", targets: [Shopping, Tasks with { Ticked = false }]);
        Assert.Equal("Tasks", r.Target?.Name);
        Assert.True(r.TargetHidden);
    }

    [Fact]
    public void Task_with_no_default_list_uses_the_first_ticked_then_the_first_list()
    {
        Assert.Equal(Shopping, Parse("task x", targets: [Archive, Shopping with { IsDefault = false }]).Target);
        var r = Parse("task x", targets: [Archive]);
        Assert.Equal(Archive, r.Target);
        Assert.True(r.TargetHidden);
    }

    [Fact]
    public void Task_by_default_needs_no_prefix()
    {
        var r = Parse("buy milk tomorrow", taskByDefault: true);
        Assert.Equal(QuickAddKind.Task, r.Kind);
        Assert.Equal("buy milk", r.Title);
        Assert.Equal(new DateTime(2026, 11, 12), r.Start.Date);
        Assert.False(r.Spans.ContainsKey(QuickAddField.Kind));
        Assert.Equal("buy milk", Parse("task buy milk", taskByDefault: true).Title);
    }

    [Fact]
    public void Without_task_by_default_the_same_phrase_is_an_event() => Assert.Equal(QuickAddKind.Event, Parse("buy milk").Kind);

    // Quick-create slot (R5)

    private static readonly DateTime Slot = D("2026-11-20 14:30");

    [Theory]
    [InlineData("Lunch", "2026-11-20 14:30", "2026-11-20 15:30")]
    [InlineData("Lunch for 30m", "2026-11-20 14:30", "2026-11-20 15:00")]
    [InlineData("Lunch tomorrow", "2026-11-12 14:30", "2026-11-12 15:30")]
    [InlineData("Lunch 1pm", "2026-11-20 13:00", "2026-11-20 14:00")]
    [InlineData("Lunch tomorrow 1pm", "2026-11-12 13:00", "2026-11-12 14:00")]
    [InlineData("Lunch 9-5", "2026-11-20 09:00", "2026-11-20 17:00")]
    public void Timed_slot_pre_sets_date_and_time_and_typed_values_override(string phrase, string start, string end)
    {
        var r = Parse(phrase, slot: Slot);
        Assert.False(r.AllDay);
        Assert.Equal(D(start), r.Start);
        Assert.Equal(D(end), r.End);
    }

    [Fact]
    public void Timed_slot_with_all_day_typed_is_all_day_on_the_slot_date()
    {
        var r = Parse("Lunch all day", slot: Slot);
        Assert.True(r.AllDay);
        Assert.Equal(D("2026-11-20"), r.Start);
        Assert.Equal(D("2026-11-21"), r.End);
    }

    [Fact]
    public void All_day_slot_stays_all_day_with_its_date()
    {
        var r = Parse("Lunch", slot: D("2026-11-20"), slotAllDay: true);
        Assert.True(r.AllDay);
        Assert.Equal(D("2026-11-20"), r.Start);
        Assert.Equal(D("2026-11-21"), r.End);
    }

    [Fact]
    public void All_day_slot_with_a_typed_date_is_all_day_on_that_date()
    {
        var r = Parse("Lunch fri", slot: D("2026-11-20"), slotAllDay: true);
        Assert.True(r.AllDay);
        Assert.Equal(D("2026-11-13"), r.Start);
    }

    [Fact]
    public void A_typed_time_on_an_all_day_slot_makes_it_timed()
    {
        var r = Parse("Lunch 3pm", slot: D("2026-11-20"), slotAllDay: true);
        Assert.False(r.AllDay);
        Assert.Equal(D("2026-11-20 15:00"), r.Start);
        Assert.Equal(D("2026-11-20 16:00"), r.End);
    }

    [Fact]
    public void Task_on_a_slot_is_due_on_the_slot_date() => Assert.Equal(new DateTime(2026, 11, 20), Parse("task x", slot: Slot).Start.Date);

    // Title and errors (R3)

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fri 1pm")]
    [InlineData("task")]
    [InlineData("task tomorrow")]
    public void An_empty_title_is_an_error(string phrase) => Assert.Equal("Add a title", Parse(phrase).Error);

    [Fact]
    public void A_good_phrase_has_no_error() => Assert.Null(Parse("Lunch").Error);

    [Fact]
    public void Title_keeps_original_case_and_collapses_gaps() => Assert.Equal("Lunch with SAM", Parse("  Lunch   with SAM  fri ").Title);

    [Fact]
    public void Only_the_first_date_is_used()
    {
        var r = Parse("Trip fri tomorrow");
        Assert.Equal(new DateTime(2026, 11, 13), r.Start);
        Assert.Equal("Trip tomorrow", r.Title);
    }

    // Spans (R7)

    [Fact]
    public void Spans_cover_each_recognised_field()
    {
        const string phrase = "Gym next fri 6-7pm in 2 days every week #Work";
        var r = Parse(phrase);
        Assert.Equal("next fri", Text(phrase, r, QuickAddField.Date));
        Assert.Equal("6-7pm", Text(phrase, r, QuickAddField.Time));
        Assert.Equal("every week", Text(phrase, r, QuickAddField.Repeat));
        Assert.Equal("#Work", Text(phrase, r, QuickAddField.Target));
        Assert.Equal("Gym in 2 days", r.Title);
    }

    // Preview chips (R4)

    [Fact]
    public void Chips_for_an_event()
    {
        var r = Parse("Lunch with Sam fri 1pm at Cafe Rio #Personal");
        Assert.Equal(["Fri 13 Nov", "13:00–14:00", "Cafe Rio", "Personal"], QuickAddParser.Chips(r));
    }

    [Fact]
    public void Chips_for_a_recurring_event()
    {
        Assert.Equal(["Wed 11 Nov", "09:15–09:30", "Work", "Every weekday"], QuickAddParser.Chips(Parse("Standup weekdays 9:15 for 15m")));
        Assert.Equal(["Wed 11 Nov", "All day", "Work", "Every day"], QuickAddParser.Chips(Parse("Pills every day")));
        Assert.Equal(["Fri 13 Nov", "All day", "Work", "Every Friday"], QuickAddParser.Chips(Parse("Gym every fri")));
        Assert.Equal(["Wed 11 Nov", "All day", "Work", "Every month"], QuickAddParser.Chips(Parse("Rent every month")));
    }

    [Fact]
    public void Chips_for_a_task()
    {
        Assert.Equal(["Task", "Thu 12 Nov", "Tasks"], QuickAddParser.Chips(Parse("task submit report tomorrow")));
    }

    [Fact]
    public void Chips_mark_a_hidden_target() =>
        Assert.Equal(["Wed 11 Nov", "All day", "Holidays (hidden)"], QuickAddParser.Chips(Parse("Lunch #Outlook/Hol")));

    // Mixed H:MM and am/pm ranges (R6)

    [Theory]
    [InlineData("Call 1:30-2pm", "2026-11-11 13:30", "2026-11-11 14:00")]
    [InlineData("Call 1:30pm-2:30", "2026-11-11 13:30", "2026-11-11 14:30")]
    [InlineData("Call noon-1:30", "2026-11-11 12:00", "2026-11-11 13:30")]
    [InlineData("Call 11:30-1pm", "2026-11-11 11:30", "2026-11-11 13:00")]
    [InlineData("Call 11:30-noon", "2026-11-11 11:30", "2026-11-11 12:00")]
    [InlineData("Call 9am-10:30", "2026-11-11 09:00", "2026-11-11 10:30")]
    [InlineData("Call 11pm-12:30", "2026-11-11 23:00", "2026-11-12 00:30")]
    [InlineData("Call 9:00-10:30", "2026-11-11 09:00", "2026-11-11 10:30")]
    [InlineData("Call 13:00-14:00", "2026-11-11 13:00", "2026-11-11 14:00")]
    public void Ranges_with_one_meridiem_and_one_colon_time(string phrase, string start, string end)
    {
        var r = Parse(phrase);
        Assert.Equal("Call", r.Title);
        Assert.Equal(D(start), r.Start);
        Assert.Equal(D(end), r.End);
    }

    // At a bare number with more words after it

    [Fact]
    public void At_a_bare_number_followed_by_lowercase_words_is_a_time()
    {
        var r = Parse("Call Bob at 3 about budget");
        Assert.Equal("Call Bob about budget", r.Title);
        Assert.Equal(D("2026-11-11 15:00"), r.Start);
        Assert.Null(r.Location);
    }

    // Odd input

    [Theory]
    [InlineData("1/1/0000")]
    [InlineData("1/1/10000")]
    [InlineData("31/12/9999")]
    [InlineData("1/1/00")]
    public void Numeric_dates_with_an_impossible_year_stay_in_the_title(string text)
    {
        var r = Parse("Meet " + text, dayFirst: true);
        if (text == "1/1/00")
        {
            Assert.Equal(new DateTime(2000, 1, 1), r.Start);
        }
        else
        {
            Assert.Equal("Meet " + text, r.Title);
        }
    }

    public static TheoryData<string> OddPhrases() =>
    [
        "", " ", "#", "/", "#/", "1/1/0000", "99:99", "at", "every", "next", "-", "12-", "-12", "@", "for", "for 99999999999 hours",
        "in", "in 99999999999 days", "task", "todo #", "all", "Café ☕ à 3 pm", "日本語 明日", "at 25", "0-0", "12:60", "13pm", "0am",
        "next next fri", "every every", "5 Dec Dec 5", "may may", "1pm to", "to 3pm", "1pm - ", "#a/b/c", "#/ /", "   fri   ",
    ];

    [Theory]
    [MemberData(nameof(OddPhrases))]
    public void Odd_phrases_never_throw(string phrase)
    {
        var options = new QuickAddOptions(true, All, D("2026-11-20 14:30"));
        var parsed = QuickAddParser.Parse(phrase, Now, options);
        QuickAddParser.Chips(parsed);
        QuickAddParser.Parse(phrase, Now, options with { DayFirst = false, TaskByDefault = true });
        for (var caret = -1; caret <= phrase.Length + 1; caret++)
        {
            QuickAddSuggest.Suggest(phrase, caret, options);
        }
    }

    // Weekly repeats start on one of their weekdays

    [Theory]
    [InlineData("Gym every fri tomorrow", "2026-11-13")]
    [InlineData("Gym every fri", "2026-11-13")]
    [InlineData("Gym every wed", "2026-11-11")]
    [InlineData("Standup weekdays sat", "2026-11-16")]
    [InlineData("Standup weekdays tomorrow", "2026-11-12")]
    [InlineData("Review every week sat", "2026-11-14")]
    public void The_start_date_moves_to_the_first_repeat_weekday(string phrase, string date)
    {
        var r = Parse(phrase);
        Assert.Equal(D(date), r.Start.Date);
        Assert.Contains(r.Start.DayOfWeek, r.Repeat.Weekdays);
    }
}
