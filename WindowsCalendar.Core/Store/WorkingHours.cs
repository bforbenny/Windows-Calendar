using System.Globalization;

namespace WindowsCalendar.Core.Store;

/// <summary>
/// Working days and hours (calendar-shading R1): a local setting for all accounts. Start is before End.
/// </summary>
public sealed record WorkingHours(IReadOnlySet<DayOfWeek> Days, TimeOnly Start, TimeOnly End)
{
    public static readonly WorkingHours Default = new(
        new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
        new TimeOnly(9, 0), new TimeOnly(17, 0));

    public bool IsWorkingDay(DateOnly date) => Days.Contains(date.DayOfWeek);

    /// <summary>Stored as "1,2,3,4,5|09:00|17:00" (days as DayOfWeek numbers).</summary>
    public string Serialize() =>
        $"{string.Join(",", Days.Order().Select(d => (int)d))}|{Start:HH\\:mm}|{End:HH\\:mm}";

    /// <summary>The stored value, or the default when it is missing or unreadable.</summary>
    public static WorkingHours Parse(string? value)
    {
        var parts = value?.Split('|');
        if (parts is not { Length: 3 }
            || !TimeOnly.TryParseExact(parts[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(parts[2], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            || end <= start)
        {
            return Default;
        }
        var days = new HashSet<DayOfWeek>();
        foreach (var d in parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(d, CultureInfo.InvariantCulture, out var n) || n is < 0 or > 6)
            {
                return Default;
            }
            days.Add((DayOfWeek)n);
        }
        return new WorkingHours(days, start, end);
    }
}
