namespace WindowsCalendar.Core.Views;

public enum ViewKind { Week, Month, Agenda, Tasks }

/// <summary>The scrollable range and virtualisation maths for the views (calendar-views R1a).</summary>
public static class ViewPeriod
{
    /// <summary>
    /// Whole weeks covering the occurrence window (-3 / +12 months from today, local-store-offline R3),
    /// starting on <paramref name="weekStart"/>.
    /// </summary>
    public static (DateOnly First, int Days) Window(DateOnly today, DayOfWeek weekStart)
    {
        var first = StartOfWeek(today.AddMonths(-3), weekStart);
        var last = today.AddMonths(12);
        var weeks = (last.DayNumber - first.DayNumber) / 7 + 1;
        return (first, weeks * 7);
    }

    public static DateOnly StartOfWeek(DateOnly date, DayOfWeek weekStart) =>
        date.AddDays(-(((int)date.DayOfWeek - (int)weekStart + 7) % 7));

    /// <summary>
    /// Indices of the items (days or weeks) to build: those in the viewport plus <paramref name="buffer"/> on each
    /// side, clamped to [0, count). Empty (First > Last) when there is nothing to show.
    /// </summary>
    public static (int First, int Last) Realized(double offset, double viewport, double itemSize, int count, int buffer)
    {
        if (count == 0 || itemSize <= 0)
        {
            return (0, -1);
        }
        var first = (int)Math.Floor(offset / itemSize) - buffer;
        var last = (int)Math.Ceiling((offset + viewport) / itemSize) + buffer;
        return (Math.Max(0, first), Math.Min(count - 1, last));
    }
}
