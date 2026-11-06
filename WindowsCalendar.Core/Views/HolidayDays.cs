namespace WindowsCalendar.Core.Views;

/// <summary>
/// Holiday names by day (calendar-shading R4): each all-day event of the holiday calendar marks every day it spans.
/// Several holidays on one day are joined with ", ".
/// </summary>
public static class HolidayDays
{
    public static IReadOnlyDictionary<DateOnly, string> Build(IEnumerable<(DateOnly First, DateOnly Last, string Name)> holidays)
    {
        var names = new Dictionary<DateOnly, List<string>>();
        foreach (var (first, last, name) in holidays)
        {
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                if (!names.TryGetValue(day, out var list))
                {
                    names[day] = list = [];
                }
                if (!list.Contains(name))
                {
                    list.Add(name);
                }
            }
        }
        return names.ToDictionary(p => p.Key, p => string.Join(", ", p.Value));
    }
}
