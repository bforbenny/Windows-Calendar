namespace WindowsCalendar.Core.Views;

/// <summary>An item the keyboard can select: a key, the days it covers, and its order within a day.</summary>
public sealed record Selectable(string Key, DateOnly First, DateOnly Last, bool AllDay, DateTime Start);

public enum SelectionMove { Up, Down, Left, Right }

/// <summary>
/// Keyboard selection between items (keyboard-navigation R2 interim). An item is listed on every day it spans; within
/// a day all-day items come first, then by start. Left/Right go to the nearest day that has items, picking the item
/// whose time of day is closest.
/// </summary>
public sealed class SelectionNav
{
    private readonly SortedDictionary<DateOnly, List<Selectable>> _days = [];

    public SelectionNav(IEnumerable<Selectable> items)
    {
        foreach (var item in items)
        {
            for (var day = item.First; day <= item.Last; day = day.AddDays(1))
            {
                if (!_days.TryGetValue(day, out var list))
                {
                    _days[day] = list = [];
                }
                list.Add(item);
            }
        }
        foreach (var list in _days.Values)
        {
            list.Sort((a, b) => a.AllDay != b.AllDay ? (a.AllDay ? -1 : 1) : a.Start.CompareTo(b.Start));
        }
    }

    /// <summary>The first item on <paramref name="today"/>, else the first from <paramref name="from"/> on.</summary>
    public (Selectable Item, DateOnly Day)? Initial(DateOnly today, DateOnly from)
    {
        if (_days.TryGetValue(today, out var list))
        {
            return (list[0], today);
        }
        foreach (var (day, items) in _days)
        {
            if (day >= from)
            {
                return (items[0], day);
            }
        }
        return null;
    }

    /// <summary>
    /// Where the selection goes from <paramref name="key"/> on <paramref name="day"/>; null when there is nowhere to
    /// go, or the item is not listed on that day.
    /// </summary>
    public (Selectable Item, DateOnly Day)? Move(string key, DateOnly day, SelectionMove move)
    {
        if (!_days.TryGetValue(day, out var list))
        {
            return null;
        }
        var index = list.FindIndex(i => i.Key == key);
        if (index < 0)
        {
            return null;
        }
        switch (move)
        {
            case SelectionMove.Up:
                return index > 0 ? (list[index - 1], day) : null;
            case SelectionMove.Down:
                return index < list.Count - 1 ? (list[index + 1], day) : null;
            default:
                var next = move == SelectionMove.Right
                    ? _days.Keys.Where(d => d > day).Cast<DateOnly?>().FirstOrDefault()
                    : _days.Keys.Where(d => d < day).Cast<DateOnly?>().LastOrDefault();
                if (next is not { } target)
                {
                    return null;
                }
                var time = TimeOfDay(list[index]);
                var nearest = _days[target].MinBy(i => Math.Abs((TimeOfDay(i) - time).Ticks))!;
                return (nearest, target);
        }
    }

    /// <summary>The day nearest <paramref name="day"/> that lists <paramref name="key"/> (after a move or a redraw).</summary>
    public DateOnly? DayOf(string key, DateOnly day) =>
        _days.Where(p => p.Value.Any(i => i.Key == key)).Select(p => (DateOnly?)p.Key)
            .MinBy(d => Math.Abs(d!.Value.DayNumber - day.DayNumber));

    private static TimeSpan TimeOfDay(Selectable item) => item.AllDay ? TimeSpan.Zero : item.Start.TimeOfDay;
}
