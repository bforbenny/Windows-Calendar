using System.Globalization;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Core.QuickAdd;

/// <summary>What a date phrase means in search, and its chip text (search-date-chips R2). Quick add uses the same ranges.</summary>
public static class SearchDates
{
    /// <summary>
    /// The whole calendar period of the phrase, as a half-open range of days: a day; a weekday within this week; a week or
    /// month, shifted; a bare month in this year. Weeks start on <paramref name="weekStart"/>.
    /// </summary>
    public static (DateOnly From, DateOnly ToExclusive) Range(DatePhrase phrase, DateOnly today, DayOfWeek weekStart)
    {
        var weekFirst = today.AddDays(-(((int)today.DayOfWeek - (int)weekStart + 7) % 7));
        switch (phrase.Kind)
        {
            case DatePhraseKind.Weekday:
                var day = weekFirst.AddDays(((int)phrase.Weekday!.Value - (int)weekStart + 7) % 7);
                return (day, day.AddDays(1));
            case DatePhraseKind.Week:
                var week = weekFirst.AddDays(phrase.Shift * 7);
                return (week, week.AddDays(7));
            case DatePhraseKind.Month:
                var month = new DateOnly(today.Year, today.Month, 1).AddMonths(phrase.Shift);
                return (month, month.AddMonths(1));
            case DatePhraseKind.MonthName:
                var named = new DateOnly(today.Year, phrase.Month!.Value, 1);
                return (named, named.AddMonths(1));
            default:
                return (phrase.Date!.Value, phrase.Date.Value.AddDays(1));
        }
    }

    /// <summary>The chip text: "Next week", "This month", "Monday", "March", or the formatted date of a day.</summary>
    public static string Label(DatePhrase phrase, (DateOnly From, DateOnly ToExclusive) range, CultureInfo culture)
    {
        var info = culture.DateTimeFormat;
        return phrase.Kind switch
        {
            DatePhraseKind.Week => phrase.Shift switch { 0 => "This week", > 0 => "Next week", _ => "Last week" },
            DatePhraseKind.Month => phrase.Shift switch { 0 => "This month", > 0 => "Next month", _ => "Last month" },
            DatePhraseKind.Weekday => info.GetDayName(phrase.Weekday!.Value),
            DatePhraseKind.MonthName => info.GetMonthName(phrase.Month!.Value),
            _ => range.From.ToDateTime(TimeOnly.MinValue).ToString(DateFormats.WeekdayMonthDay(culture), culture),
        };
    }
}
