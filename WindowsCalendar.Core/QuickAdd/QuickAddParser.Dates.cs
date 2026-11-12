using System.Text.RegularExpressions;

namespace WindowsCalendar.Core.QuickAdd;

public static partial class QuickAddParser
{
    private static readonly Dictionary<string, DayOfWeek> WeekdayNames = new()
    {
        ["mon"] = DayOfWeek.Monday, ["monday"] = DayOfWeek.Monday,
        ["tue"] = DayOfWeek.Tuesday, ["tues"] = DayOfWeek.Tuesday, ["tuesday"] = DayOfWeek.Tuesday,
        ["wed"] = DayOfWeek.Wednesday, ["wednesday"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday, ["thur"] = DayOfWeek.Thursday, ["thurs"] = DayOfWeek.Thursday, ["thursday"] = DayOfWeek.Thursday,
        ["fri"] = DayOfWeek.Friday, ["friday"] = DayOfWeek.Friday,
        ["sat"] = DayOfWeek.Saturday, ["saturday"] = DayOfWeek.Saturday,
        ["sun"] = DayOfWeek.Sunday, ["sunday"] = DayOfWeek.Sunday,
    };

    private static readonly Dictionary<string, int> MonthNames = new()
    {
        ["jan"] = 1, ["january"] = 1, ["feb"] = 2, ["february"] = 2, ["mar"] = 3, ["march"] = 3, ["apr"] = 4, ["april"] = 4,
        ["may"] = 5, ["jun"] = 6, ["june"] = 6, ["jul"] = 7, ["july"] = 7, ["aug"] = 8, ["august"] = 8,
        ["sep"] = 9, ["sept"] = 9, ["september"] = 9, ["oct"] = 10, ["october"] = 10, ["nov"] = 11, ["november"] = 11,
        ["dec"] = 12, ["december"] = 12,
    };

    /// <summary>R6: short forms that are common words.</summary>
    private static readonly HashSet<string> CommonWordWeekdays = ["sun", "sat", "wed"];

    private static readonly HashSet<string> CommonWordMonths = ["mar", "may"];

    private static readonly Regex DayRegex = new(@"^(\d{1,2})(?:st|nd|rd|th)?$", RegexOptions.CultureInvariant);
    private static readonly Regex NumericDateRegex = new(@"^(\d{1,2})/(\d{1,2})(?:/(\d{4}|\d{2}))?$", RegexOptions.CultureInvariant);

    /// <summary>Whether the word is a weekday or month name the parser takes as a date word on its own.</summary>
    internal static bool IsDateWord(string word) => WeekdayNames.ContainsKey(word) || MonthNames.ContainsKey(word);

    /// <summary>R2: "weekdays" and "every day/week/month/weekday-name".</summary>
    private static void ReadRepeat(Scan scan, Found found)
    {
        for (var i = 0; i < scan.Count; i++)
        {
            if (!scan.IsFree(i))
            {
                continue;
            }
            var word = scan.Lower(i);
            if (word == "weekdays")
            {
                found.Repeat = Recurrence.Weekdays;
                scan.Claim(QuickAddField.Repeat, i, i);
                return;
            }
            if (word != "every" || !scan.IsFree(i + 1))
            {
                continue;
            }
            var next = scan.Lower(i + 1);
            if (next == "day")
            {
                found.Repeat = Recurrence.Daily;
            }
            else if (next == "week")
            {
                found.Repeat = Recurrence.Weekly;
            }
            else if (next == "month")
            {
                found.Repeat = Recurrence.Monthly;
            }
            else if (WeekdayNames.TryGetValue(next, out var day))
            {
                found.Repeat = Recurrence.Weekly;
                found.RepeatDay = day;
            }
            else
            {
                continue;
            }
            scan.Claim(QuickAddField.Repeat, i, i + 1);
            return;
        }
    }

    /// <summary>
    /// R2/R6: the first date in the phrase. The plain pass takes unambiguous words; the common-word pass then takes
    /// sun, sat, wed, mar, may only when they are the last word or next to a date or time. A range ("this week") is its
    /// first day still to come for an event and its last day for a task (R2 Ranges).
    /// </summary>
    private static void ReadDate(Scan scan, Found found, DateOnly today, bool dayFirst, bool commonWords, DayOfWeek weekStart)
    {
        for (var i = 0; i < scan.Count && found.Date is null; i++)
        {
            if (scan.IsFree(i) && TryDate(scan, i, today, dayFirst, commonWords, false, out var phrase, out var first, out var last))
            {
                switch (phrase!.Kind)
                {
                    case DatePhraseKind.Weekday:
                        found.Date = NextWeekday(today, phrase.Weekday!.Value);
                        break;
                    case DatePhraseKind.Week or DatePhraseKind.Month:
                        var range = SearchDates.Range(phrase, today, weekStart);
                        found.Date = range.From > today ? range.From : today;
                        found.DateEnd = range.ToExclusive.AddDays(-1);
                        break;
                    default:
                        found.Date = phrase.Date;
                        break;
                }
                scan.Claim(QuickAddField.Date, first, last);
            }
        }
    }

    /// <summary>
    /// search-date-chips R1: the first date phrase in <paramref name="text"/>, by the same scan, anchoring and order as
    /// quick add's date, plus "last week/month" and a bare month name, which only search reads. Null when there is none.
    /// </summary>
    public static DatePhrase? FindDatePhrase(string text, DateOnly today, bool dayFirst)
    {
        var scan = new Scan(text);
        foreach (var commonWords in new[] { false, true })
        {
            for (var i = 0; i < scan.Count; i++)
            {
                if (TryDate(scan, i, today, dayFirst, commonWords, true, out var phrase, out _, out _))
                {
                    return phrase;
                }
            }
        }
        return null;
    }

    private static bool TryDate(Scan scan, int i, DateOnly today, bool dayFirst, bool commonWords, bool search,
        out DatePhrase? phrase, out int first, out int last)
    {
        phrase = null;
        first = last = i;
        var word = scan.Lower(i);
        if (WeekdayNames.TryGetValue(word, out var day) && CommonWordWeekdays.Contains(word) == commonWords)
        {
            phrase = Phrase(scan, DatePhraseKind.Weekday, i, i, weekday: day);
            return !commonWords || scan.Anchored(i, i);
        }
        if (MonthNames.TryGetValue(word, out var month) && CommonWordMonths.Contains(word) == commonWords)
        {
            if (TryMonthPair(scan, i, month, today, out var pair, out first, out last))
            {
                phrase = Phrase(scan, DatePhraseKind.Day, first, last, date: pair);
                return !commonWords || scan.Anchored(first, last);
            }
            if (!search)
            {
                return false;
            }
            phrase = Phrase(scan, DatePhraseKind.MonthName, i, i, month: month);
            return !commonWords || scan.Anchored(i, i);
        }
        if (commonWords)
        {
            return false;
        }
        switch (word)
        {
            case "today":
                phrase = Phrase(scan, DatePhraseKind.Day, i, i, date: today);
                return true;
            case "tomorrow":
                phrase = Phrase(scan, DatePhraseKind.Day, i, i, date: today.AddDays(1));
                return true;
            case "next" when scan.IsFree(i + 1) && WeekdayNames.TryGetValue(scan.Lower(i + 1), out var nextDay):
                last = i + 1;
                phrase = Phrase(scan, DatePhraseKind.Day, i, last, date: NextWeekday(today, nextDay).AddDays(7));
                return true;
            case "this" or "next" or "last" when (word != "last" || search) && scan.IsFree(i + 1) && scan.Lower(i + 1) is "week" or "month":
                last = i + 1;
                phrase = Phrase(scan, scan.Lower(last) == "week" ? DatePhraseKind.Week : DatePhraseKind.Month, i, last,
                    shift: word == "this" ? 0 : word == "next" ? 1 : -1);
                return true;
            case "in" when scan.IsFree(i + 1) && scan.IsFree(i + 2) && int.TryParse(scan.Lower(i + 1), out var n) && n is >= 0 and <= 3650:
                var unit = scan.Lower(i + 2);
                if (unit is not ("day" or "days" or "week" or "weeks"))
                {
                    return false;
                }
                last = i + 2;
                phrase = Phrase(scan, DatePhraseKind.Day, i, last, date: today.AddDays(unit.StartsWith("week", StringComparison.Ordinal) ? n * 7 : n));
                return true;
        }
        var numeric = NumericDateRegex.Match(word);
        if (numeric.Success && TryNumericDate(numeric, dayFirst, today, out var numericDate))
        {
            phrase = Phrase(scan, DatePhraseKind.Day, i, i, date: numericDate);
            return true;
        }
        return false;
    }

    private static DatePhrase Phrase(Scan scan, DatePhraseKind kind, int first, int last, DateOnly? date = null,
        DayOfWeek? weekday = null, int shift = 0, int? month = null) =>
        new(kind, new Range(scan.Tokens[first].Start, scan.Tokens[last].End), date, weekday, shift, month);

    /// <summary>"5 Dec" or "Dec 5"; a day-month already past this year means next year (R6).</summary>
    private static bool TryMonthPair(Scan scan, int i, int month, DateOnly today, out DateOnly date, out int first, out int last)
    {
        foreach (var dayIndex in (int[])[i - 1, i + 1])
        {
            var match = scan.IsFree(dayIndex) ? DayRegex.Match(scan.Lower(dayIndex)) : Match.Empty;
            if (match.Success && ResolveDayMonth(int.Parse(match.Groups[1].Value), month, today) is { } resolved)
            {
                date = resolved;
                first = Math.Min(i, dayIndex);
                last = Math.Max(i, dayIndex);
                return true;
            }
        }
        date = default;
        first = last = i;
        return false;
    }

    private static bool TryNumericDate(Match match, bool dayFirst, DateOnly today, out DateOnly date)
    {
        var a = int.Parse(match.Groups[1].Value);
        var b = int.Parse(match.Groups[2].Value);
        var (day, month) = dayFirst ? (a, b) : (b, a);
        date = default;
        if (!match.Groups[3].Success)
        {
            if (ResolveDayMonth(day, month, today) is not { } resolved)
            {
                return false;
            }
            date = resolved;
            return true;
        }
        var year = int.Parse(match.Groups[3].Value);
        year = match.Groups[3].Value.Length == 2 ? 2000 + year : year;
        if (month is < 1 or > 12 || day < 1 || year is < 1 or > 9998 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }
        date = new DateOnly(year, month, day);
        return true;
    }

    /// <summary>This year's day-month, or the next year it exists in when it has already passed (R6).</summary>
    private static DateOnly? ResolveDayMonth(int day, int month, DateOnly today)
    {
        if (month is < 1 or > 12 || day < 1)
        {
            return null;
        }
        for (var year = today.Year; year <= today.Year + 8; year++)
        {
            if (day <= DateTime.DaysInMonth(year, month) && new DateOnly(year, month, day) >= today)
            {
                return new DateOnly(year, month, day);
            }
        }
        return null;
    }

    /// <summary>The next <paramref name="day"/> on or after <paramref name="from"/> (today counts).</summary>
    private static DateOnly NextWeekday(DateOnly from, DayOfWeek day) => from.AddDays(((int)day - (int)from.DayOfWeek + 7) % 7);
}
