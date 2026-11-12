namespace WindowsCalendar.Core.QuickAdd;

/// <summary>A whitespace-delimited word of the phrase. <see cref="Core"/> drops trailing punctuation for matching.</summary>
internal sealed record Token(string Text, int Start, int End)
{
    public string Core => Text.TrimEnd(',', '.', ';', '!', '?');

    public string Lower => Core.ToLowerInvariant();
}

/// <summary>The phrase as tokens, which of them the rules have used, and the span each field came from (quick-add R3, R7).</summary>
internal sealed class Scan
{
    private readonly bool[] _used;
    private readonly bool[] _temporal;

    public Scan(string phrase)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < phrase.Length)
        {
            if (char.IsWhiteSpace(phrase[i]))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < phrase.Length && !char.IsWhiteSpace(phrase[i]))
            {
                i++;
            }
            tokens.Add(new Token(phrase[start..i], start, i));
        }
        Tokens = tokens;
        _used = new bool[tokens.Count];
        _temporal = new bool[tokens.Count];
    }

    public IReadOnlyList<Token> Tokens { get; }

    public int Count => Tokens.Count;

    public Dictionary<QuickAddField, Range> Spans { get; } = [];

    public bool IsFree(int i) => i >= 0 && i < Count && !_used[i];

    /// <summary>Whether token <paramref name="i"/> is part of a date or time (R6 "next to another date or time token").</summary>
    public bool IsTemporal(int i) => i >= 0 && i < Count && _temporal[i];

    public string Lower(int i) => Tokens[i].Lower;

    /// <summary>
    /// R6: a short form that is a common word counts when it is the last word (only recognised tokens such as `#Work` may
    /// follow) or next to a date or time.
    /// </summary>
    public bool Anchored(int first, int last)
    {
        var lastWord = true;
        for (var k = last + 1; k < Count; k++)
        {
            lastWord &= _used[k];
        }
        return lastWord || IsTemporal(first - 1) || IsTemporal(last + 1);
    }

    public void Claim(QuickAddField field, int first, int last)
    {
        for (var k = first; k <= last; k++)
        {
            _used[k] = true;
            _temporal[k] = field is QuickAddField.Date or QuickAddField.Time;
        }
        Spans[field] = new Range(Tokens[first].Start, Tokens[last].End);
    }
}

internal enum Recurrence { None, Daily, Weekdays, Weekly, Monthly }

/// <summary>What the passes found, before it is laid out as dates.</summary>
internal sealed class Found
{
    public DateOnly? Date { get; set; }

    /// <summary>The last day of a range date ("next week"), which is a task's due date.</summary>
    public DateOnly? DateEnd { get; set; }

    /// <summary>Minutes from midnight; <see cref="EndMin"/> may pass 1440 when a range ends on the next day.</summary>
    public int? StartMin { get; set; }

    public int? EndMin { get; set; }

    public int? DurationMin { get; set; }

    public bool AllDay { get; set; }

    public Recurrence Repeat { get; set; }

    /// <summary>The weekday of "every &lt;weekday&gt;".</summary>
    public DayOfWeek? RepeatDay { get; set; }

    public string? TargetText { get; set; }

    public string? Location { get; set; }
}
