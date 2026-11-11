using System.Globalization;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Core.Tests;

/// <summary>The tray flyout's list and "Next" line (tray-flyout R3, R3a, R4a).</summary>
public sealed class FlyoutListTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Today = new(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);

    private static OccurrenceView Event(string title, double startHours, double hours, bool allDay = false, string collection = "c1") =>
        new(title, collection, ItemKind.Event, title, null, At(startHours), At(startHours + hours), allDay);

    // Whole minutes, so the countdown tests do not depend on floating-point rounding.
    private static DateTimeOffset At(double hours) => Today.AddMinutes(Math.Round(hours * 60));

    private static Item Task(string title, int dueInDays, bool completed = false) =>
        new(title, "t1", ItemKind.Task, null, "{}", Summary: title, DueUtc: Today.AddDays(dueInDays), AllDay: true,
            Status: completed ? "completed" : "notStarted");

    private static FlyoutContent Build(params OccurrenceView[] occurrences) =>
        FlyoutList.Build(occurrences, [], "holidays", Now, Zone);

    private static string[] Titles(IEnumerable<OccurrenceView> list) => list.Select(o => o.Summary!).ToArray();

    [Fact]
    public void Sections_InProgress_LaterToday_Tomorrow_EndedLeftOut()
    {
        var content = Build(
            Event("ended", 8, 1),
            Event("standup", 9.5, 1),
            Event("lunch", 12, 1),
            Event("starts now", 10, 0.5),
            Event("review", 15, 1),
            Event("tomorrow call", 33, 1),
            Event("day after", 57, 1));

        Assert.Equal(["standup", "starts now"], Titles(content.InProgress));
        Assert.Equal(["lunch", "review"], Titles(content.LaterToday));
        Assert.Equal(["tomorrow call"], Titles(content.Tomorrow));
    }

    [Fact]
    public void MultiDayEvents_ShowOnce_InTheFirstSection()
    {
        var content = Build(
            Event("conference", -24, 72, allDay: true),
            Event("starts tomorrow", 24, 48, allDay: true),
            Event("late night", 23, 2),
            Event("long timed", 9, 30),
            Event("tomorrow timed", 34, 1));

        Assert.Equal(["conference", "long timed"], Titles(content.AllDay));
        Assert.Equal(["late night"], Titles(content.LaterToday));
        // Tomorrow: its all-day events first, without the ones already shown today, then its timed ones.
        Assert.Equal(["starts tomorrow", "tomorrow timed"], Titles(content.Tomorrow));
    }

    [Fact]
    public void AllDayEndingAtMidnight_IsNotOnTomorrow()
    {
        var content = Build(Event("yesterday", -24, 24, allDay: true), Event("today", 0, 24, allDay: true));
        Assert.Equal(["today"], Titles(content.AllDay));
        Assert.Empty(content.Tomorrow);
    }

    [Fact]
    public void Holiday_NamesTheDay_InsteadOfARow()
    {
        var content = Build(
            Event("Melbourne Cup", 0, 24, allDay: true, collection: "holidays"),
            Event("tomorrow holiday", 24, 24, allDay: true, collection: "holidays"),
            Event("offsite", 0, 24, allDay: true));

        Assert.Equal("Melbourne Cup", content.Holiday);
        Assert.Equal(["offsite"], Titles(content.AllDay));
        Assert.Empty(content.Tomorrow);
    }

    [Fact]
    public void Tasks_OpenAndDueTodayOrEarlier_OverdueFirst()
    {
        var content = FlyoutList.Build([],
            [Task("today b", 0), Task("today a", 0), Task("overdue", -3), Task("tomorrow", 1), Task("done", 0, completed: true)],
            null, Now, Zone);
        Assert.Equal(["overdue", "today a", "today b"], content.Tasks.Select(t => t.Summary!).ToArray());
    }

    [Fact]
    public void TaskOccurrences_AreIgnored_TasksComeFromTheTaskList()
    {
        var task = new OccurrenceView("t", "t1", ItemKind.Task, "task", null, Now.AddHours(1), Now.AddHours(1), false);
        var content = FlyoutList.Build([task], [], null, Now, Zone);
        Assert.Empty(content.LaterToday);
        Assert.Empty(content.Next);
    }

    [Fact]
    public void Next_IsTheNextTimedEventToday_InMinutesAndHours()
    {
        var culture = CultureInfo.InvariantCulture;
        Assert.Equal("Next: standup in 12 min",
            FlyoutList.NextText(Build(Event("all day", 0, 24, allDay: true), Event("standup", 10.2, 0.5)), Now, Zone, culture));
        Assert.Equal("Next: lunch in 2 h",
            FlyoutList.NextText(Build(Event("lunch", 12, 1)), Now, Zone, culture));
        Assert.Equal("Next: review in 1 h 5 min",
            FlyoutList.NextText(Build(Event("review", 11 + 5 / 60.0, 1)), Now, Zone, culture));
    }

    [Fact]
    public void Next_NamesEventsStartingTogether()
    {
        var culture = CultureInfo.InvariantCulture;
        Assert.Equal("Next: a and b in 30 min",
            FlyoutList.NextText(Build(Event("b", 10.5, 1), Event("a", 10.5, 0.5), Event("c", 11, 1)), Now, Zone, culture));
        Assert.Equal("Next: a and 2 more in 30 min",
            FlyoutList.NextText(Build(Event("a", 10.5, 1), Event("b", 10.5, 1), Event("c", 10.5, 1)), Now, Zone, culture));
    }

    [Fact]
    public void Next_LooksIntoTomorrow_ThenNothingElseToday()
    {
        var culture = CultureInfo.InvariantCulture;
        var tomorrow = Build(Event("in progress", 9, 2), Event("tomorrow all day", 24, 24, allDay: true), Event("standup", 33, 0.5));
        Assert.True(tomorrow.NextIsTomorrow);
        Assert.Equal("Next: tomorrow 09:00 standup", FlyoutList.NextText(tomorrow, Now, Zone, culture));

        var nothing = Build(Event("in progress", 9, 2), Event("tomorrow all day", 24, 24, allDay: true));
        Assert.False(nothing.NextIsTomorrow);
        Assert.Equal("Nothing else today", FlyoutList.NextText(nothing, Now, Zone, culture));
    }

    [Fact]
    public void Days_FollowTheZone_NotUtc()
    {
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("AUS Eastern Standard Time");
        // 10:00 UTC is 21:00 in Sydney (UTC+11 in November); an event at 14:00 UTC is 01:00 tomorrow there.
        var content = FlyoutList.Build([Event("late", 14, 1)], [], null, Now, sydney);
        Assert.Empty(content.LaterToday);
        Assert.Equal(["late"], Titles(content.Tomorrow));
    }
}
