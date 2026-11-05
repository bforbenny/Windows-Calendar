using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Core.Tests;

public sealed class ViewLayoutTests
{
    [Fact]
    public void StartOfWeek_UsesConfiguredDay()
    {
        // Thursday 5 Nov 2026.
        Assert.Equal(new DateOnly(2026, 11, 2), ViewPeriod.StartOfWeek(new DateOnly(2026, 11, 5), DayOfWeek.Monday));
        Assert.Equal(new DateOnly(2026, 11, 1), ViewPeriod.StartOfWeek(new DateOnly(2026, 11, 5), DayOfWeek.Sunday));
        Assert.Equal(new DateOnly(2026, 11, 2), ViewPeriod.StartOfWeek(new DateOnly(2026, 11, 2), DayOfWeek.Monday));
    }

    [Fact]
    public void Window_IsWholeWeeksFromThreeMonthsBackToTwelveAhead()
    {
        var (first, days) = ViewPeriod.Window(new DateOnly(2026, 11, 5), DayOfWeek.Monday);

        // 5 Aug 2026 is a Wednesday: the window starts on Monday 3 Aug.
        Assert.Equal(new DateOnly(2026, 8, 3), first);
        Assert.Equal(0, days % 7);
        var last = first.AddDays(days - 1);
        Assert.True(last >= new DateOnly(2027, 11, 5) && last < new DateOnly(2027, 11, 12));
    }

    [Fact]
    public void Realized_AddsBufferAndClamps()
    {
        // 100 px items, viewport 0..700 shows items 0-6 (7 is touched by the ceiling).
        Assert.Equal((0, 9), ViewPeriod.Realized(0, 700, 100, 50, 2));
        Assert.Equal((8, 20), ViewPeriod.Realized(1050, 700, 100, 50, 2));
        Assert.Equal((43, 49), ViewPeriod.Realized(4500, 700, 100, 50, 2));
        Assert.Equal((0, -1), ViewPeriod.Realized(0, 700, 100, 0, 2));
    }

    [Fact]
    public void Columns_SeparateClustersAndShareCountWithinCluster()
    {
        // 9-10 and 9:30-11 overlap; 10-10:30 fits under the first; 12-13 is alone.
        var result = EventLayout.Columns([(540, 600), (570, 660), (600, 630), (720, 780)]);

        Assert.Equal([(0, 2), (1, 2), (0, 2), (0, 1)], result);
    }

    [Fact]
    public void Columns_ThreeWayOverlap()
    {
        var result = EventLayout.Columns([(540, 660), (550, 600), (560, 620)]);

        Assert.Equal([(0, 3), (1, 3), (2, 3)], result);
    }

    [Fact]
    public void Columns_TouchingEventsDoNotOverlap()
    {
        Assert.Equal([(0, 1), (0, 1)], EventLayout.Columns([(540, 600), (600, 660)]));
    }

    [Fact]
    public void Lanes_ReuseRowsWhenFree()
    {
        // Mon-Wed, Tue, Thu-Fri, Mon: the Monday event takes row 1, and Tuesday fits after it in row 1.
        var rows = EventLayout.Lanes([(0, 2), (1, 1), (3, 4), (0, 0)]);

        Assert.Equal([0, 1, 0, 1], rows);
    }
}
