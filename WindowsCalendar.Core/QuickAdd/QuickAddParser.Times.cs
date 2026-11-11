using System.Text.RegularExpressions;

namespace WindowsCalendar.Core.QuickAdd;

public static partial class QuickAddParser
{
    /// <summary>One time of day as typed. <see cref="Explicit"/> is false for a bare number (R6). Meridiem is 'a', 'p' or none.</summary>
    private readonly record struct TimePart(int Hour, int Minute, char Meridiem, bool Explicit, bool Word = false)
    {
        /// <summary>Says what part of the day it is: am/pm, noon or midnight.</summary>
        public bool Anchored => Meridiem != '\0' || Word;

        /// <summary>H:MM with hour 1-12 and no am/pm, so it could be either half of the day.</summary>
        public bool Loose => !Anchored && Explicit && Hour is >= 1 and <= 12;
    }

    private static readonly Regex TimeRegex = new(@"^(\d{1,2})(?::(\d{2}))?(am|pm)?$", RegexOptions.CultureInvariant);
    private static readonly Regex DurationRegex = new(@"^(\d{1,4})(m|min|mins|minute|minutes|h|hr|hrs|hour|hours)$", RegexOptions.CultureInvariant);
    private static readonly Regex UnitRegex = new(@"^(m|min|mins|minute|minutes|h|hr|hrs|hour|hours)$", RegexOptions.CultureInvariant);

    private static readonly char[] Dashes = ['-', '–'];

    /// <summary>R2: "all day".</summary>
    private static void ReadAllDay(Scan scan, Found found)
    {
        for (var i = 0; i + 1 < scan.Count; i++)
        {
            if (scan.IsFree(i) && scan.IsFree(i + 1) && scan.Lower(i) == "all" && scan.Lower(i + 1) == "day")
            {
                found.AllDay = true;
                scan.Claim(QuickAddField.AllDay, i, i + 1);
                return;
            }
        }
    }

    /// <summary>R2/R6: a range (`1-2pm`, `1pm to 3pm`, `9-5`) is looked for first, then a single time (`1pm`, `13:00`, `noon`).</summary>
    private static void ReadTime(Scan scan, Found found)
    {
        for (var i = 0; i < scan.Count; i++)
        {
            if (scan.IsFree(i) && TryRange(scan, i, out var start, out var end, out var length))
            {
                found.StartMin = start;
                found.EndMin = end;
                scan.Claim(QuickAddField.Time, WithAt(scan, i), i + length - 1);
                return;
            }
        }
        for (var i = 0; i < scan.Count; i++)
        {
            if (scan.IsFree(i) && ParsePart(scan.Lower(i)) is { Explicit: true } part)
            {
                found.StartMin = Literal(part);
                scan.Claim(QuickAddField.Time, WithAt(scan, i), i);
                return;
            }
        }
    }

    /// <summary>R6: a bare number is a time after `at` (`at 3`) unless the next word is capitalised, so it starts a location (`at 12 Main St`).</summary>
    private static void ReadBareAtTime(Scan scan, Found found)
    {
        for (var i = 0; i + 1 < scan.Count && found.StartMin is null; i++)
        {
            if (scan.IsFree(i) && scan.Lower(i) == "at" && scan.IsFree(i + 1)
                && !(scan.IsFree(i + 2) && char.IsUpper(scan.Tokens[i + 2].Text[0])) && ParsePart(scan.Lower(i + 1)) is { Explicit: false } part)
            {
                found.StartMin = DefaultHour(part.Hour) * 60;
                scan.Claim(QuickAddField.Time, i, i + 1);
            }
        }
    }

    /// <summary>R2: "for 30m", "for 1h", "for 90 min", "for 2 hours". A range wins over a duration (R6), so this is skipped then.</summary>
    private static void ReadDuration(Scan scan, Found found)
    {
        for (var i = 0; i + 1 < scan.Count; i++)
        {
            if (!scan.IsFree(i) || scan.Lower(i) != "for" || !scan.IsFree(i + 1))
            {
                continue;
            }
            var joined = DurationRegex.Match(scan.Lower(i + 1));
            if (joined.Success)
            {
                found.DurationMin = Minutes(joined.Groups[1].Value, joined.Groups[2].Value);
                scan.Claim(QuickAddField.Duration, i, i + 1);
                return;
            }
            if (scan.IsFree(i + 2) && scan.Lower(i + 1).Length <= 4 && int.TryParse(scan.Lower(i + 1), out _) && UnitRegex.IsMatch(scan.Lower(i + 2)))
            {
                found.DurationMin = Minutes(scan.Lower(i + 1), scan.Lower(i + 2));
                scan.Claim(QuickAddField.Duration, i, i + 2);
                return;
            }
        }
    }

    private static int Minutes(string number, string unit) => int.Parse(number) * (unit[0] == 'h' ? 60 : 1);

    /// <summary>The first token of a time, taking a free "at" just before it along (so `at 3pm` is a time, not a location).</summary>
    private static int WithAt(Scan scan, int i) => scan.IsFree(i - 1) && scan.Lower(i - 1) == "at" ? i - 1 : i;

    private static bool TryRange(Scan scan, int i, out int start, out int end, out int length)
    {
        start = end = 0;
        length = 1;
        var word = scan.Lower(i);
        var dash = word.IndexOfAny(Dashes);
        if (dash > 0 && dash < word.Length - 1 && ParsePart(word[..dash]) is { } a && ParsePart(word[(dash + 1)..]) is { } b)
        {
            (start, end) = ResolveRange(a, b);
            return true;
        }
        if (ParsePart(word) is { } from && scan.IsFree(i + 1) && scan.IsFree(i + 2)
            && scan.Lower(i + 1) is "to" or "-" or "–" && ParsePart(scan.Lower(i + 2)) is { } to)
        {
            (start, end) = ResolveRange(from, to);
            length = 3;
            return true;
        }
        return false;
    }

    private static TimePart? ParsePart(string word)
    {
        if (word == "noon")
        {
            return new TimePart(12, 0, '\0', true, true);
        }
        if (word == "midnight")
        {
            return new TimePart(0, 0, '\0', true, true);
        }
        var match = TimeRegex.Match(word);
        if (!match.Success)
        {
            return null;
        }
        var hour = int.Parse(match.Groups[1].Value);
        var minute = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        var meridiem = match.Groups[3].Success ? match.Groups[3].Value[0] : '\0';
        if (minute > 59 || (meridiem != '\0' ? hour is < 1 or > 12 : hour > 23))
        {
            return null;
        }
        return new TimePart(hour, minute, meridiem, meridiem != '\0' || match.Groups[2].Success);
    }

    /// <summary>Minutes from midnight for a time that says what it is (am/pm, a colon, noon, midnight).</summary>
    private static int Literal(TimePart p) => p.Meridiem switch
    {
        'a' => p.Hour % 12 * 60 + p.Minute,
        'p' => (p.Hour % 12 + 12) * 60 + p.Minute,
        _ => p.Hour * 60 + p.Minute,
    };

    /// <summary>R6 for a bare number: 1-6 is pm, 7-11 is am, 12 is noon.</summary>
    private static int DefaultHour(int hour) => hour is >= 1 and <= 6 ? hour + 12 : hour;

    private static int[] Readings(TimePart p) => [p.Hour % 12 * 60 + p.Minute, p.Hour % 12 * 60 + p.Minute + 12 * 60];

    /// <summary>The earliest reading of an H:MM time after <paramref name="start"/>; none fits means the next day.</summary>
    private static int EarliestAfter(TimePart p, int start)
    {
        var after = Readings(p).Where(r => r > start).ToList();
        return after.Count > 0 ? after.Min() : Readings(p).Min() + 24 * 60;
    }

    /// <summary>The latest reading of an H:MM time before <paramref name="end"/>; none fits means the earliest.</summary>
    private static int LatestBefore(TimePart p, int end)
    {
        var before = Readings(p).Where(r => r < end).ToList();
        return before.Count > 0 ? before.Max() : Readings(p).Min();
    }

    /// <summary>R6: a range whose end is before its start ends on the next day; a bare end takes the later reading that fits.</summary>
    private static (int Start, int End) ResolveRange(TimePart a, TimePart b)
    {
        int start, end;
        if (a.Anchored && b.Loose)
        {
            start = Literal(a);
            end = EarliestAfter(b, start);
        }
        else if (a.Loose && b.Anchored)
        {
            end = Literal(b);
            start = LatestBefore(a, end);
        }
        else if (a.Explicit && b.Explicit)
        {
            (start, end) = (Literal(a), Literal(b));
        }
        else if (!a.Explicit && b.Meridiem != '\0')
        {
            end = Literal(b);
            start = Literal(a with { Meridiem = b.Meridiem });
            if (start >= end)
            {
                start = Literal(a with { Meridiem = b.Meridiem == 'p' ? 'a' : 'p' });
            }
        }
        else if (!a.Explicit && b.Explicit)
        {
            (start, end) = (DefaultHour(a.Hour) * 60, Literal(b));
        }
        else
        {
            start = a.Explicit ? Literal(a) : DefaultHour(a.Hour) * 60;
            end = DefaultHour(b.Hour) * 60;
            if (end <= start && b.Hour is >= 1 and <= 11)
            {
                var other = end >= 12 * 60 ? end - 12 * 60 : end + 12 * 60;
                end = other > start ? other : end;
            }
        }
        return (start, end < start ? end + 24 * 60 : end);
    }
}
