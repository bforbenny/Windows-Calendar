using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Core.Tests;

/// <summary>Keyboard selection between items (keyboard-navigation R2 interim).</summary>
public sealed class SelectionNavTests
{
    private static readonly DateOnly Mon = new(2026, 11, 2);

    private static Selectable Timed(string key, DateOnly day, int hour) =>
        new(key, day, day, false, day.ToDateTime(new TimeOnly(hour, 0)));

    private static Selectable AllDay(string key, DateOnly first, DateOnly last) =>
        new(key, first, last, true, first.ToDateTime(TimeOnly.MinValue));

    private readonly SelectionNav _nav = new([
        Timed("mon-9", Mon, 9),
        Timed("mon-14", Mon, 14),
        AllDay("trip", Mon, Mon.AddDays(1)),
        Timed("tue-13", Mon.AddDays(1), 13),
        Timed("thu-8", Mon.AddDays(3), 8),
        Timed("thu-16", Mon.AddDays(3), 16),
    ]);

    [Fact]
    public void Initial_IsTodaysFirst_AllDayFirst()
    {
        Assert.Equal(("trip", Mon), Key(_nav.Initial(Mon, Mon)));
        // No items today (Wed): the first from the day in view on.
        Assert.Equal(("thu-8", Mon.AddDays(3)), Key(_nav.Initial(Mon.AddDays(2), Mon.AddDays(2))));
        Assert.Null(new SelectionNav([]).Initial(Mon, Mon));
    }

    [Fact]
    public void UpDown_StayInTheDay()
    {
        Assert.Equal(("mon-9", Mon), Key(_nav.Move("trip", Mon, SelectionMove.Down)));
        Assert.Equal(("mon-14", Mon), Key(_nav.Move("mon-9", Mon, SelectionMove.Down)));
        Assert.Null(_nav.Move("mon-14", Mon, SelectionMove.Down));
        Assert.Null(_nav.Move("trip", Mon, SelectionMove.Up));
    }

    [Fact]
    public void LeftRight_GoToTheNearestTimeOnTheNextDayWithItems()
    {
        // Mon 14:00 -> Tue: the all-day trip (00:00) or 13:00; 13:00 is nearer.
        Assert.Equal(("tue-13", Mon.AddDays(1)), Key(_nav.Move("mon-14", Mon, SelectionMove.Right)));
        // Tue 13:00 -> Thu (Wed is empty): 16:00 is nearer than 08:00.
        Assert.Equal(("thu-16", Mon.AddDays(3)), Key(_nav.Move("tue-13", Mon.AddDays(1), SelectionMove.Right)));
        Assert.Equal(("tue-13", Mon.AddDays(1)), Key(_nav.Move("thu-16", Mon.AddDays(3), SelectionMove.Left)));
        Assert.Null(_nav.Move("thu-16", Mon.AddDays(3), SelectionMove.Right));
    }

    [Fact]
    public void DayOf_FindsTheNearestDayListingTheItem()
    {
        Assert.Equal(Mon.AddDays(1), _nav.DayOf("trip", Mon.AddDays(2)));
        Assert.Null(_nav.DayOf("gone", Mon));
    }

    private static (string, DateOnly)? Key((Selectable Item, DateOnly Day)? r) => r is { } x ? (x.Item.Key, x.Day) : null;
}
