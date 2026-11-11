namespace WindowsCalendar.Core.QuickAdd;

/// <summary>
/// One completion for the word at the caret (quick-add R4a). <see cref="Insert"/> replaces the phrase text in
/// <see cref="Replace"/>; the UI adds the trailing space. <see cref="Target"/> is set for calendar and list suggestions.
/// </summary>
public sealed record QuickAddSuggestion(string Label, string Detail, string Insert, Range Replace, QuickAddTarget? Target);

/// <summary>Completions for the word at the caret (quick-add R4a): calendars after `#`, else the fixed R2 date and time vocabulary.</summary>
public static class QuickAddSuggest
{
    private const int MaxPhrases = 6;

    private static readonly string[] Weekdays = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];

    public static IReadOnlyList<QuickAddSuggestion> Suggest(string phrase, int caret, QuickAddOptions options)
    {
        caret = Math.Clamp(caret, 0, phrase.Length);
        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(phrase[start - 1]))
        {
            start--;
        }
        var end = caret;
        while (end < phrase.Length && !char.IsWhiteSpace(phrase[end]))
        {
            end++;
        }
        var typed = phrase[start..caret];
        if (typed.Length == 0)
        {
            return [];
        }
        return typed[0] == '#'
            ? Calendars(typed[1..], new Range(start, end), QuickAddParser.IsTaskPhrase(phrase, options), options)
            : Phrases(phrase, start, end, typed.ToLowerInvariant());
    }

    /// <summary>
    /// Ticked targets first in the R2 match order, inserting `#` and the full name without spaces (or `#account/name` when that would resolve elsewhere); then unticked
    /// ones, inserting `#account/name` (spaces removed).
    /// </summary>
    private static List<QuickAddSuggestion> Calendars(string text, Range replace, bool task, QuickAddOptions options)
    {
        var pool = options.Targets.Where(t => t.IsTaskList == task).ToList();
        var ticked = pool.Where(t => t.Ticked).OrderByDescending(t => t.IsDefault).ToList();
        var slash = text.IndexOf('/');
        bool Matches(QuickAddTarget t) => slash < 0
            ? QuickAddParser.PrefixMatch(t.Name, text)
            : QuickAddParser.PrefixMatch(t.AccountName, text[..slash]) && QuickAddParser.PrefixMatch(t.Name, text[(slash + 1)..]);

        // The insert must resolve back to the same target; a suggestion that no form can reach is skipped.
        var suggestions = new List<QuickAddSuggestion>();
        foreach (var target in ticked.Where(Matches).Concat(pool.Where(t => !t.Ticked && Matches(t))))
        {
            string[] forms = target.Ticked ? ["#" + QuickAddParser.StripSpaces(target.Name), AccountInsert(target)] : [AccountInsert(target)];
            var insert = forms.FirstOrDefault(f => QuickAddParser.ResolveTarget(f[1..], task, options.Targets).Target?.Id == target.Id);
            if (insert is not null)
            {
                suggestions.Add(new QuickAddSuggestion(target.Name, target.AccountName, insert, replace, target));
            }
        }
        return suggestions;
    }

    private static string AccountInsert(QuickAddTarget t) =>
        "#" + QuickAddParser.StripSpaces(t.AccountName) + "/" + QuickAddParser.StripSpaces(t.Name);

    /// <summary>
    /// A word of 2+ letters that starts a vocabulary phrase; or, after `next`, `every` or `all`, or `in <n>`, the start of
    /// the phrase's last word (so `next f` offers `next friday`). An exact, complete match is not offered.
    /// </summary>
    private static List<QuickAddSuggestion> Phrases(string phrase, int start, int end, string typed)
    {
        if (QuickAddParser.IsDateWord(typed))
        {
            return [];
        }
        var previous = PreviousWords(phrase, start);
        var lead = previous.Count > 0 ? previous[0].Text.ToLowerInvariant() : "";
        IEnumerable<(string Text, int From)> candidates;
        string typedText;
        if (lead is "next" or "every" or "all")
        {
            candidates = Vocabulary().Where(p => p.StartsWith(lead + " ", StringComparison.Ordinal)).Select(p => (p, previous[0].Start));
            typedText = lead + " " + typed;
        }
        else if (previous.Count > 1 && previous[1].Text.Equals("in", StringComparison.OrdinalIgnoreCase) && lead.All(char.IsAsciiDigit))
        {
            candidates = new[] { "days", "weeks" }.Select(unit => ($"in {lead} {unit}", previous[1].Start));
            typedText = $"in {lead} {typed}";
        }
        else
        {
            candidates = typed.Length >= 2 ? Vocabulary().Where(p => !p.Contains(' ')).Select(p => (p, start)) : [];
            typedText = typed;
        }

        return candidates
            .Where(c => c.Text.StartsWith(typedText, StringComparison.Ordinal) && c.Text != typedText)
            .Take(MaxPhrases)
            .Select(c => new QuickAddSuggestion(c.Text, "", c.Text, new Range(c.From, end), null))
            .ToList();
    }

    /// <summary>The words before <paramref name="start"/>, nearest first, at most two.</summary>
    private static List<(string Text, int Start)> PreviousWords(string phrase, int start)
    {
        var words = new List<(string, int)>();
        var i = start;
        while (words.Count < 2)
        {
            while (i > 0 && char.IsWhiteSpace(phrase[i - 1]))
            {
                i--;
            }
            var wordEnd = i;
            while (i > 0 && !char.IsWhiteSpace(phrase[i - 1]))
            {
                i--;
            }
            if (wordEnd == i)
            {
                break;
            }
            words.Add((phrase[i..wordEnd], i));
        }
        return words;
    }

    /// <summary>The fixed R2 vocabulary in offer order (the "in n days/weeks" forms are built from the typed number).</summary>
    private static IEnumerable<string> Vocabulary()
    {
        yield return "today";
        yield return "tomorrow";
        foreach (var day in Weekdays)
        {
            yield return day;
        }
        foreach (var day in Weekdays)
        {
            yield return "next " + day;
        }
        foreach (var day in Weekdays)
        {
            yield return "every " + day;
        }
        yield return "every day";
        yield return "every week";
        yield return "every month";
        yield return "weekdays";
        yield return "noon";
        yield return "midnight";
        yield return "all day";
    }
}
