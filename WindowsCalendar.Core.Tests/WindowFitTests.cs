using System.Globalization;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Core.Tests;

/// <summary>Saved window placement and fit-to-screen (display-scaling R3); short date patterns (ui-style R7).</summary>
public sealed class WindowFitTests
{
    private static readonly PixelRect Work = new(0, 0, 2256, 1456);

    [Fact]
    public void Placement_round_trips()
    {
        var text = WindowFit.Format(true, new PixelRect(-1800, 40, -200, 1000));
        Assert.Equal("max;-1800,40,-200,1000", text);
        Assert.Equal((true, new PixelRect(-1800, 40, -200, 1000)), WindowFit.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("normal;1,2,3")]
    [InlineData("big;0,0,10,10")]
    [InlineData("normal;0,0,0,10")]
    [InlineData("normal;a,0,10,10")]
    public void Malformed_placement_is_ignored(string? text) => Assert.Null(WindowFit.Parse(text));

    [Fact]
    public void A_window_that_fits_stays_put() =>
        Assert.Equal(new PixelRect(100, 100, 1500, 1000), WindowFit.Fit(new PixelRect(100, 100, 1500, 1000), Work, centre: false));

    [Fact]
    public void A_window_past_the_edge_is_moved_and_shrunk() =>
        // 2800 x 1600 from the external monitor, now on the 2256 x 1456 work area.
        Assert.Equal(new PixelRect(0, 0, 2256, 1456), WindowFit.Fit(new PixelRect(1900, 200, 4700, 1800), Work, centre: false));

    [Fact]
    public void A_window_partly_off_the_right_is_moved_in() =>
        Assert.Equal(new PixelRect(856, 100, 2256, 1000), WindowFit.Fit(new PixelRect(1500, 100, 2900, 1000), Work, centre: false));

    [Fact]
    public void Centre_places_the_window_in_the_middle() =>
        Assert.Equal(new PixelRect(428, 228, 1828, 1228), WindowFit.Fit(new PixelRect(-3000, 0, -1600, 1000), Work, centre: true));

    [Theory]
    [InlineData("en-GB", "13 Nov", "Fri 13 Nov")]
    [InlineData("en-US", "Nov 13", "Fri Nov 13")]
    public void Date_patterns_follow_the_region(string name, string monthDay, string weekday)
    {
        var culture = CultureInfo.GetCultureInfo(name);
        var day = new DateTime(2026, 11, 13);
        Assert.Equal(monthDay, day.ToString(DateFormats.MonthDay(culture), culture));
        Assert.Equal(weekday, day.ToString(DateFormats.WeekdayMonthDay(culture), culture));
    }
}
