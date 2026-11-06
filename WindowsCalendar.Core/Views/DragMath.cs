namespace WindowsCalendar.Core.Views;

/// <summary>
/// Where a dragged event lands (calendar-views R7). Times are local wall-clock; all-day ends are exclusive dates.
/// </summary>
public static class DragMath
{
    public static readonly TimeSpan Snap = TimeSpan.FromMinutes(15);

    /// <summary>The shortest a resize can make an event.</summary>
    public static readonly TimeSpan MinLength = Snap;

    /// <summary>A time rounded to the nearest 15 minutes.</summary>
    public static DateTime SnapTime(DateTime t) =>
        new(Round(t.Ticks), t.Kind);

    /// <summary>Moves by the pointer's travel since it grabbed the event, rounded to 15 minutes; the length is kept.</summary>
    public static (DateTime Start, DateTime End) Move(DateTime start, DateTime end, DateTime grabbedAt, DateTime pointer)
    {
        var delta = TimeSpan.FromTicks(Round((pointer - grabbedAt).Ticks));
        return (start + delta, end + delta);
    }

    /// <summary>Drags the start edge; it stays at least 15 minutes before the end.</summary>
    public static (DateTime Start, DateTime End) ResizeStart(DateTime start, DateTime end, DateTime pointer)
    {
        var s = SnapTime(pointer);
        return (s > end - MinLength ? end - MinLength : s, end);
    }

    /// <summary>Drags the end edge; it stays at least 15 minutes after the start.</summary>
    public static (DateTime Start, DateTime End) ResizeEnd(DateTime start, DateTime end, DateTime pointer)
    {
        var e = SnapTime(pointer);
        return (start, e < start + MinLength ? start + MinLength : e);
    }

    /// <summary>Month and Agenda: the same times on a day <paramref name="days"/> later (or earlier).</summary>
    public static (DateTime Start, DateTime End) MoveDays(DateTime start, DateTime end, int days) =>
        (start.AddDays(days), end.AddDays(days));

    /// <summary>From the all-day lane into the grid: a 1-hour timed event at the drop point.</summary>
    public static (DateTime Start, DateTime End) ToTimed(DateTime pointer)
    {
        var s = SnapTime(pointer);
        return (s, s.AddHours(1));
    }

    /// <summary>From the grid into the all-day lane: an all-day event on that date.</summary>
    public static (DateTime Start, DateTime End) ToAllDay(DateOnly day) =>
        (day.ToDateTime(TimeOnly.MinValue), day.AddDays(1).ToDateTime(TimeOnly.MinValue));

    private static long Round(long ticks) => (long)Math.Round((double)ticks / Snap.Ticks, MidpointRounding.AwayFromZero) * Snap.Ticks;
}
