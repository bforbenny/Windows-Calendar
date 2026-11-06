using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Core.Tests;

public sealed class DragMathTests
{
    private static DateTime T(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0);

    [Fact]
    public void Move_SnapsTravelTo15MinutesAndKeepsLength()
    {
        // Grabbed at 09:20, dropped at 11:28 on the next day: travel 1 day 2:08 rounds to 1 day 2:15.
        var (s, e) = DragMath.Move(T(5, 9, 0), T(5, 10, 30), T(5, 9, 20), T(6, 11, 28));

        Assert.Equal((T(6, 11, 15), T(6, 12, 45)), (s, e));
    }

    [Fact]
    public void Move_BackwardsRoundsToo()
    {
        var (s, _) = DragMath.Move(T(5, 9, 0), T(5, 10, 0), T(5, 9, 0), T(5, 7, 52));

        Assert.Equal(T(5, 7, 45), s);
    }

    [Fact]
    public void Resize_SnapsAndKeepsMinimumLength()
    {
        Assert.Equal((T(5, 8, 15), T(5, 10, 0)), DragMath.ResizeStart(T(5, 9, 0), T(5, 10, 0), T(5, 8, 20)));
        Assert.Equal((T(5, 9, 45), T(5, 10, 0)), DragMath.ResizeStart(T(5, 9, 0), T(5, 10, 0), T(5, 11, 0)));
        Assert.Equal((T(5, 9, 0), T(5, 12, 30)), DragMath.ResizeEnd(T(5, 9, 0), T(5, 10, 0), T(5, 12, 35)));
        Assert.Equal((T(5, 9, 0), T(5, 9, 15)), DragMath.ResizeEnd(T(5, 9, 0), T(5, 10, 0), T(5, 6, 0)));
    }

    [Fact]
    public void LaneAndGridConversions()
    {
        Assert.Equal((T(7, 14, 0), T(7, 15, 0)), DragMath.ToTimed(T(7, 13, 53)));
        Assert.Equal((T(7, 0), T(8, 0)), DragMath.ToAllDay(new DateOnly(2026, 11, 7)));
        Assert.Equal((T(3, 0), T(5, 0)), DragMath.MoveDays(T(5, 0), T(7, 0), -2));
    }
}
