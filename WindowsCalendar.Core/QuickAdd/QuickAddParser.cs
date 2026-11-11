using System.Globalization;
using WindowsCalendar.Core.CalDav;

namespace WindowsCalendar.Core.QuickAdd;

/// <summary>
/// Turns a typed phrase into an event or task (quick-add R2, R3, R5-R7). Pure: the clock, short-date order and calendar
/// names all come in as parameters. The phrase is split into words and ordered passes claim the words they recognise;
/// the words left over are the title.
/// </summary>
public static partial class QuickAddParser
{
    private const int DefaultDurationMinutes = 60;

    public static QuickAddResult Parse(string phrase, DateTime now, QuickAddOptions options)
    {
        var scan = new Scan(phrase);
        var found = new Found();
        var task = options.TaskByDefault || HasTaskPrefix(scan);
        if (HasTaskPrefix(scan))
        {
            scan.Claim(QuickAddField.Kind, 0, 0);
        }
        var today = DateOnly.FromDateTime(now);

        ReadTarget(scan, found);
        if (!task)
        {
            ReadRepeat(scan, found);
            ReadAllDay(scan, found);
            ReadTime(scan, found);
            if (found.EndMin is null)
            {
                ReadDuration(scan, found);
            }
        }
        ReadDate(scan, found, today, options.DayFirst, commonWords: false);
        ReadDate(scan, found, today, options.DayFirst, commonWords: true);
        if (!task)
        {
            ReadBareAtTime(scan, found);
            ReadLocation(scan, found);
        }

        var title = string.Join(' ', Enumerable.Range(0, scan.Count).Where(scan.IsFree).Select(i => scan.Tokens[i].Text));
        var (target, targetError) = ResolveTarget(found.TargetText, task, options.Targets);
        var slotDate = options.SlotStart is { } slot ? DateOnly.FromDateTime(slot) : today;
        var date = found.Date ?? slotDate;
        if (!task)
        {
            date = AlignToRepeat(found, date);
        }
        var (allDay, start, end) = task ? DueDate(date) : Layout(found, date, options);
        return new QuickAddResult(
            task ? QuickAddKind.Task : QuickAddKind.Event, title, allDay, start, end,
            found.Location, target, target is { Ticked: false },
            task ? RepeatRule.None : BuildRepeat(found, date), scan.Spans,
            title.Length == 0 ? "Add a title" : targetError);
    }

    /// <summary>The preview chips (R4), e.g. "Fri 6 Nov", "13:00–14:00", "Cafe Rio", "Work", "Every weekday".</summary>
    public static IReadOnlyList<string> Chips(QuickAddResult result)
    {
        var culture = CultureInfo.InvariantCulture;
        var chips = new List<string>();
        if (result.Kind == QuickAddKind.Task)
        {
            chips.Add("Task");
        }
        chips.Add(result.Start.ToString("ddd d MMM", culture));
        if (result.Kind == QuickAddKind.Event)
        {
            chips.Add(result.AllDay ? "All day" : result.Start.ToString("HH:mm", culture) + "–" + result.End.ToString("HH:mm", culture));
            if (result.Location is not null)
            {
                chips.Add(result.Location);
            }
        }
        if (result.Target is { } target)
        {
            chips.Add(result.TargetHidden ? target.Name + " (hidden)" : target.Name);
        }
        if (result.Repeat.Frequency != RepeatFrequency.None)
        {
            chips.Add(RepeatLabel(result.Repeat));
        }
        return chips;
    }

    /// <summary>Whether the phrase is a task: a leading `task ` or `todo ` (R2), or the Tasks view's default (R5).</summary>
    internal static bool IsTaskPhrase(string phrase, QuickAddOptions options) => options.TaskByDefault || HasTaskPrefix(new Scan(phrase));

    private static bool HasTaskPrefix(Scan scan) => scan.Count > 0 && scan.Lower(0) is "task" or "todo";

    /// <summary>Prefix match that ignores case and spaces (R4a), so `#CafeRio` matches "Cafe Rio".</summary>
    internal static bool PrefixMatch(string name, string prefix) =>
        StripSpaces(name).StartsWith(StripSpaces(prefix), StringComparison.OrdinalIgnoreCase);

    internal static bool ExactMatch(string name, string text) =>
        string.Equals(StripSpaces(name), StripSpaces(text), StringComparison.OrdinalIgnoreCase);

    internal static string StripSpaces(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>R2: the first `#name` or `#account/name`.</summary>
    private static void ReadTarget(Scan scan, Found found)
    {
        for (var i = 0; i < scan.Count; i++)
        {
            if (scan.IsFree(i) && scan.Tokens[i].Core.Length > 1 && scan.Tokens[i].Core[0] == '#')
            {
                found.TargetText = scan.Tokens[i].Core[1..];
                scan.Claim(QuickAddField.Target, i, i);
                return;
            }
        }
    }

    /// <summary>
    /// R2: the default target, or the `#` match. `#name` tries ticked targets of the right kind, the default first;
    /// `#account/name` tries those, then unticked ones. Within a tier an exact name (and account) beats a prefix match.
    /// With no `#`: a task uses the default list even when unticked, else the first ticked, else the first; an event uses
    /// the default calendar if ticked, else the first ticked, else (none ticked) the default, else the first.
    /// </summary>
    internal static (QuickAddTarget? Target, string? Error) ResolveTarget(string? text, bool task, IReadOnlyList<QuickAddTarget> targets)
    {
        var pool = targets.Where(t => t.IsTaskList == task).ToList();
        var ticked = pool.Where(t => t.Ticked).OrderByDescending(t => t.IsDefault).ToList();
        var what = task ? "task list" : "calendar";
        if (text is null)
        {
            var pick = task
                ? pool.FirstOrDefault(t => t.IsDefault) ?? ticked.FirstOrDefault() ?? pool.FirstOrDefault()
                : ticked.FirstOrDefault() ?? pool.FirstOrDefault(t => t.IsDefault) ?? pool.FirstOrDefault();
            return (pick, pick is null ? $"No {what} to add to" : null);
        }
        var slash = text.IndexOf('/');
        QuickAddTarget? match;
        if (slash < 0)
        {
            match = ticked.Where(t => PrefixMatch(t.Name, text)).OrderBy(t => ExactMatch(t.Name, text) ? 0 : 1).FirstOrDefault();
        }
        else
        {
            var account = text[..slash];
            var name = text[(slash + 1)..];
            QuickAddTarget? InTier(IEnumerable<QuickAddTarget> tier) => tier
                .Where(t => PrefixMatch(t.AccountName, account) && PrefixMatch(t.Name, name))
                .OrderBy(t => ExactMatch(t.AccountName, account) ? 0 : 1).ThenBy(t => ExactMatch(t.Name, name) ? 0 : 1)
                .FirstOrDefault();
            match = InTier(ticked) ?? InTier(pool.Where(t => !t.Ticked));
        }
        return (match, match is null ? $"No {what} matches #{text}" : null);
    }

    /// <summary>R2: text after ` @ `, or after ` at ` when the next word starts with a capital or a digit and is not a time.</summary>
    private static void ReadLocation(Scan scan, Found found)
    {
        for (var i = 0; i + 1 < scan.Count; i++)
        {
            if (!scan.IsFree(i) || !scan.IsFree(i + 1))
            {
                continue;
            }
            var next = scan.Tokens[i + 1];
            var marker = scan.Lower(i) == "at"
                && (char.IsUpper(next.Text[0]) || char.IsAsciiDigit(next.Text[0]))
                && ParsePart(next.Lower) is not { Explicit: true };
            if (!marker && scan.Tokens[i].Text != "@")
            {
                continue;
            }
            var last = i + 1;
            while (scan.IsFree(last + 1))
            {
                last++;
            }
            found.Location = string.Join(' ', Enumerable.Range(i + 1, last - i).Select(k => scan.Tokens[k].Text)).TrimEnd(',', ';');
            scan.Claim(QuickAddField.Location, i, last);
            return;
        }
    }

    /// <summary>
    /// With a weekly repeat on named weekdays the start moves forward to the first of them (as ChangeRule does), so
    /// "every fri tomorrow" on a Wednesday starts Friday and "weekdays sat" starts Monday.
    /// </summary>
    private static DateOnly AlignToRepeat(Found found, DateOnly date)
    {
        var rule = BuildRepeat(found, date);
        if (rule.Frequency != RepeatFrequency.Weekly)
        {
            return date;
        }
        for (var i = 0; i < 7 && !rule.Weekdays.Contains(date.DayOfWeek); i++)
        {
            date = date.AddDays(1);
        }
        return date;
    }

    /// <summary>A task has only a due date (R2): the day, with no time.</summary>
    private static (bool AllDay, DateTime Start, DateTime End) DueDate(DateOnly date)
    {
        var start = date.ToDateTime(TimeOnly.MinValue);
        return (true, start, start.AddDays(1));
    }

    /// <summary>
    /// R5: a typed time makes a timed event (also on an all-day slot); a typed date or nothing on a timed slot keeps the
    /// slot's time; otherwise it is all-day.
    /// </summary>
    private static (bool AllDay, DateTime Start, DateTime End) Layout(Found found, DateOnly date, QuickAddOptions options)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue);
        var duration = found.DurationMin ?? DefaultDurationMinutes;
        if (!found.AllDay && found.StartMin is { } startMin)
        {
            var start = midnight.AddMinutes(startMin);
            return (false, start, found.EndMin is { } endMin ? midnight.AddMinutes(endMin) : start.AddMinutes(duration));
        }
        if (!found.AllDay && options.SlotStart is { } slot && !options.SlotAllDay)
        {
            var start = midnight + slot.TimeOfDay;
            return (false, start, start.AddMinutes(duration));
        }
        return (true, midnight, midnight.AddDays(1));
    }

    private static RepeatRule BuildRepeat(Found found, DateOnly date) => found.Repeat switch
    {
        Recurrence.Daily => new RepeatRule(RepeatFrequency.Daily, 1, [], null, null, null),
        Recurrence.Weekdays => new RepeatRule(RepeatFrequency.Weekly, 1,
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], null, null, null),
        Recurrence.Weekly => new RepeatRule(RepeatFrequency.Weekly, 1, [found.RepeatDay ?? date.DayOfWeek], null, null, null),
        Recurrence.Monthly => new RepeatRule(RepeatFrequency.Monthly, 1, [], null, null, null),
        _ => RepeatRule.None,
    };

    private static string RepeatLabel(RepeatRule rule)
    {
        var culture = CultureInfo.InvariantCulture;
        return rule.Frequency switch
        {
            RepeatFrequency.Daily => "Every day",
            RepeatFrequency.Monthly => "Every month",
            RepeatFrequency.Weekly when rule.Weekdays.Count == 5 && !rule.Weekdays.Contains(DayOfWeek.Saturday)
                && !rule.Weekdays.Contains(DayOfWeek.Sunday) => "Every weekday",
            RepeatFrequency.Weekly => "Every " + string.Join(", ", rule.Weekdays.Select(d => culture.DateTimeFormat.GetDayName(d))),
            _ => "Every " + rule.Frequency.ToString().ToLowerInvariant(),
        };
    }
}
