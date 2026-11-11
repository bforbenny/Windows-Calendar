using WindowsCalendar.Core.QuickAdd;

namespace WindowsCalendar.Core.Tests;

/// <summary>Quick-add suggestions for the word at the caret (quick-add R4a).</summary>
public sealed class QuickAddSuggestTests
{
    private static readonly QuickAddTarget Work = new("w", "Work", "Fastmail", true, true, false);
    private static readonly QuickAddTarget Workshop = new("ws", "Workshop", "Fastmail", true, false, false);
    private static readonly QuickAddTarget Personal = new("p", "Personal", "Fastmail", true, false, false);
    private static readonly QuickAddTarget CafeRio = new("cr", "Cafe Rio", "Home Net", true, false, false);
    private static readonly QuickAddTarget CafeRoma = new("cm", "Cafe Roma", "Home Net", true, false, false);
    private static readonly QuickAddTarget Holidays = new("h", "Holidays", "Outlook", false, false, false);
    private static readonly QuickAddTarget Tasks = new("t1", "Tasks", "Microsoft", true, true, true);
    private static readonly QuickAddTarget Shopping = new("t2", "Shopping", "Microsoft", true, false, true);
    private static readonly QuickAddTarget Archive = new("t3", "Archive", "Microsoft", false, false, true);

    private static readonly QuickAddOptions Options = new(true, [Personal, Work, Workshop, CafeRio, CafeRoma, Holidays, Tasks, Shopping, Archive]);

    private static IReadOnlyList<QuickAddSuggestion> Suggest(string phrase, int? caret = null, QuickAddOptions? options = null) =>
        QuickAddSuggest.Suggest(phrase, caret ?? phrase.Length, options ?? Options);

    private static string[] Inserts(string phrase, int? caret = null) => Suggest(phrase, caret).Select(s => s.Insert).ToArray();

    // Calendars

    [Fact]
    public void Hash_lists_ticked_calendars_default_first_then_unticked()
    {
        var all = Suggest("lunch #");
        Assert.Equal([Work, Personal, Workshop, CafeRio, CafeRoma, Holidays], all.Select(s => s.Target));
    }

    [Fact]
    public void Hash_name_narrows_by_prefix_and_inserts_the_full_name()
    {
        var s = Assert.Single(Suggest("lunch #pe"));
        Assert.Equal("Personal", s.Label);
        Assert.Equal("Fastmail", s.Detail);
        Assert.Equal("#Personal", s.Insert);
        Assert.Equal(Personal, s.Target);
        Assert.Equal("#pe", "lunch #pe"[s.Replace]);
    }

    [Fact]
    public void Hash_wo_offers_work_then_workshop()
    {
        var wo = Suggest("lunch #wo");
        Assert.Equal([Work, Workshop], wo.Select(s => s.Target));
        Assert.Equal("#Work", wo[0].Insert);
        Assert.Equal("#Workshop", wo[1].Insert);
    }

    [Fact]
    public void An_exact_name_wins_over_an_earlier_prefix_so_the_plain_form_still_resolves()
    {
        var options = new QuickAddOptions(true, [Workshop, Personal, Work with { IsDefault = false }]);
        var wo = Suggest("lunch #wo", options: options);
        Assert.Equal(["#Workshop", "#Work"], wo.Select(s => s.Insert));
        AssertResolves(wo, options);
    }

    [Fact]
    public void A_same_named_calendar_in_another_account_inserts_the_account_form()
    {
        var other = new QuickAddTarget("w9", "Work", "Outlook", true, false, false);
        var options = new QuickAddOptions(true, [Work, other]);
        var s = Suggest("lunch #wo", options: options);
        Assert.Equal(["#Work", "#Outlook/Work"], s.Select(x => x.Insert));
        AssertResolves(s, options);
    }

    [Fact]
    public void A_calendar_no_form_can_reach_is_skipped()
    {
        var twin = new QuickAddTarget("w9", "Work", "Fastmail", true, false, false);
        var options = new QuickAddOptions(true, [Work, twin]);
        Assert.Equal(Work, Assert.Single(Suggest("lunch #wo", options: options)).Target);
    }

    [Fact]
    public void Account_names_that_are_prefixes_of_each_other_resolve_to_the_right_calendar()
    {
        var pro = new QuickAddTarget("a", "Home", "Fastmail Pro", true, false, false);
        var plain = new QuickAddTarget("b", "Home", "Fastmail", true, false, false);
        var gone = new QuickAddTarget("c", "Home", "Fastmail Old", false, false, false);
        var options = new QuickAddOptions(true, [pro, plain, gone]);
        var s = Suggest("lunch #ho", options: options);
        Assert.Equal([pro, plain, gone], s.Select(x => x.Target));
        Assert.Equal(["#Home", "#Fastmail/Home", "#FastmailOld/Home"], s.Select(x => x.Insert));
        AssertResolves(s, options);
        AssertResolves(Suggest("lunch #fastmailpro/h", options: options), options);
    }

    [Fact]
    public void Every_suggestion_in_the_default_set_resolves_back_to_its_target()
    {
        AssertResolves(Suggest("lunch #"), Options);
        AssertResolves(Suggest("task x #"), Options);
    }

    private static void AssertResolves(IReadOnlyList<QuickAddSuggestion> suggestions, QuickAddOptions options)
    {
        Assert.NotEmpty(suggestions);
        foreach (var s in suggestions)
        {
            Assert.Equal(s.Target!.Id, QuickAddParser.ResolveTarget(s.Insert[1..], s.Target.IsTaskList, options.Targets).Target?.Id);
        }
    }

    [Theory]
    [InlineData("lunch fri")]
    [InlineData("lunch wed")]
    [InlineData("lunch mon")]
    [InlineData("lunch tue")]
    [InlineData("lunch thu")]
    [InlineData("lunch sat")]
    [InlineData("lunch sun")]
    [InlineData("lunch next fri")]
    [InlineData("lunch every mon")]
    public void A_word_the_parser_already_recognises_is_not_offered(string phrase) => Assert.Empty(Suggest(phrase));

    [Fact]
    public void Unticked_calendars_insert_the_account_form()
    {
        var s = Assert.Single(Suggest("lunch #hol"));
        Assert.Equal(Holidays, s.Target);
        Assert.Equal("#Outlook/Holidays", s.Insert);
    }

    [Fact]
    public void Names_with_spaces_insert_the_full_name_without_spaces()
    {
        var cafe = Suggest("lunch #caferi");
        Assert.Equal(CafeRio, Assert.Single(cafe).Target);
        Assert.Equal("#CafeRio", cafe[0].Insert);

        var both = Suggest("lunch #cafe");
        Assert.Equal([CafeRio, CafeRoma], both.Select(s => s.Target));
        Assert.Equal("#CafeRio", both[0].Insert);
        Assert.Equal("#CafeRoma", both[1].Insert);

        Assert.Equal(CafeRio, Assert.Single(Suggest("lunch #homenet/caferi")).Target);
    }

    [Fact]
    public void Account_form_narrows_by_account_and_name()
    {
        var s = Assert.Single(Suggest("lunch #outlook/h"));
        Assert.Equal(Holidays, s.Target);
        Assert.Empty(Suggest("lunch #nope/h"));
    }

    [Fact]
    public void Hash_in_a_task_phrase_lists_task_lists()
    {
        var all = Suggest("task milk #");
        Assert.Equal([Tasks, Shopping, Archive], all.Select(s => s.Target));
        Assert.Equal("#Microsoft/Archive", all[2].Insert);
        Assert.Equal("#Tasks", all[0].Insert);
        Assert.Equal("#Shopping", Assert.Single(Suggest("todo milk #s")).Insert);
    }

    [Fact]
    public void Hash_in_the_tasks_view_lists_task_lists()
    {
        var options = Options with { TaskByDefault = true };
        Assert.Equal([Tasks, Shopping, Archive], Suggest("milk #", options: options).Select(s => s.Target));
    }

    [Fact]
    public void Hash_with_no_match_offers_nothing() => Assert.Empty(Suggest("lunch #zzz"));

    // Dates and times

    [Fact]
    public void Tom_offers_tomorrow()
    {
        var s = Assert.Single(Suggest("lunch tom"));
        Assert.Equal("tomorrow", s.Insert);
        Assert.Equal("tomorrow", s.Label);
        Assert.Equal("", s.Detail);
        Assert.Null(s.Target);
        Assert.Equal("tom", "lunch tom"[s.Replace]);
    }

    [Fact]
    public void To_offers_today_then_tomorrow() => Assert.Equal(["today", "tomorrow"], Inserts("to"));

    [Fact]
    public void Next_f_offers_next_friday_replacing_both_words()
    {
        const string phrase = "lunch next f";
        var s = Assert.Single(Suggest(phrase));
        Assert.Equal("next friday", s.Insert);
        Assert.Equal("next f", phrase[s.Replace]);
    }

    [Fact]
    public void Fr_offers_friday() => Assert.Equal(["friday"], Inserts("lunch fr"));

    [Fact]
    public void No_offers_noon() => Assert.Equal(["noon"], Inserts("lunch no"));

    [Fact]
    public void Every_m_offers_monday_then_month()
    {
        const string phrase = "gym every m";
        var s = Suggest(phrase);
        Assert.Equal(["every monday", "every month"], s.Select(x => x.Insert));
        Assert.All(s, x => Assert.Equal("every m", phrase[x.Replace]));
    }

    [Fact]
    public void Week_offers_weekdays() => Assert.Equal(["weekdays"], Inserts("standup week"));

    [Fact]
    public void All_d_offers_all_day() => Assert.Equal(["all day"], Inserts("holiday all d"));

    [Theory]
    [InlineData("lunch in 2 d", "in 2 days")]
    [InlineData("lunch in 3 w", "in 3 weeks")]
    public void In_n_offers_days_or_weeks(string phrase, string insert)
    {
        var s = Assert.Single(Suggest(phrase));
        Assert.Equal(insert, s.Insert);
        Assert.Equal(phrase["lunch ".Length..], phrase[s.Replace]);
    }

    [Fact]
    public void Mi_offers_midnight() => Assert.Equal(["midnight"], Inserts("mi"));

    [Fact]
    public void A_one_letter_word_offers_nothing()
    {
        Assert.Empty(Suggest("lunch f"));
        Assert.Empty(Suggest("lunch t"));
    }

    [Fact]
    public void An_exact_complete_match_is_not_offered()
    {
        Assert.Empty(Suggest("lunch tomorrow"));
        Assert.Empty(Suggest("lunch next friday"));
        Assert.Empty(Suggest("lunch weekdays"));
        Assert.Empty(Suggest("lunch in 2 days"));
    }

    [Fact]
    public void A_keyword_alone_offers_nothing() => Assert.Empty(Suggest("gym every"));

    [Fact]
    public void Every_t_offers_tuesday_then_thursday() => Assert.Equal(["every tuesday", "every thursday"], Inserts("lunch every t"));

    [Fact]
    public void Nothing_for_unknown_words_or_an_empty_phrase()
    {
        Assert.Empty(Suggest("lunch xyz"));
        Assert.Empty(Suggest(""));
        Assert.Empty(Suggest("lunch "));
    }

    [Fact]
    public void Caret_in_the_middle_uses_the_word_before_the_caret_and_replaces_the_whole_word()
    {
        const string phrase = "lunch tom fri";
        var s = Assert.Single(Suggest(phrase, caret: 9));
        Assert.Equal("tomorrow", s.Insert);
        Assert.Equal("tom", phrase[s.Replace]);

        const string midWord = "lunch tomx fri";
        var inner = Assert.Single(Suggest(midWord, caret: 9));
        Assert.Equal("tomx", midWord[inner.Replace]);
    }

    [Fact]
    public void Caret_after_a_hash_word_in_the_middle_of_the_phrase()
    {
        const string phrase = "lunch #pe fri 1pm";
        var s = Assert.Single(Suggest(phrase, caret: 9));
        Assert.Equal(Personal, s.Target);
        Assert.Equal("#pe", phrase[s.Replace]);
    }

    [Fact]
    public void Caret_at_the_start_of_a_word_offers_nothing() => Assert.Empty(Suggest("lunch tom", caret: 6));
}
