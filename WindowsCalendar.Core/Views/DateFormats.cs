using System.Globalization;

namespace WindowsCalendar.Core.Views;

/// <summary>
/// Short date patterns whose day and month order follows the Windows region (ui-style R7): "d MMM" when the short
/// date puts the day before the month, else "MMM d". The order is read from the short-date pattern (as quick add
/// does), since Windows lets the user change that pattern but not the month-day one.
/// </summary>
public static class DateFormats
{
    /// <summary>Whether the culture's short date puts the day before the month.</summary>
    public static bool DayFirst(CultureInfo culture)
    {
        var pattern = culture.DateTimeFormat.ShortDatePattern;
        return pattern.IndexOf('d') < pattern.IndexOf('M');
    }

    /// <summary>Day and abbreviated month, e.g. "13 Nov" or "Nov 13".</summary>
    public static string MonthDay(CultureInfo culture) => DayFirst(culture) ? "d MMM" : "MMM d";

    /// <summary>Weekday, day and abbreviated month, e.g. "Fri 13 Nov" or "Fri Nov 13".</summary>
    public static string WeekdayMonthDay(CultureInfo culture) => "ddd " + MonthDay(culture);
}
