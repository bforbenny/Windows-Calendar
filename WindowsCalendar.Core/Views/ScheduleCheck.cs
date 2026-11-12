using System.Globalization;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Views;

/// <summary>An overlap (a warning) or a gap shorter than the minimum (a softer note), with the text to show.</summary>
public sealed record ScheduleNote(bool Overlap, string Text);

/// <summary>
/// A problem the views mark (schedule-gap-warning R11): the overlapping minutes, or the short gap (Start = End for
/// back-to-back events), with the text for its tooltip and screen-reader name. <paramref name="First"/> and
/// <paramref name="Second"/> are the two occurrences' keys (<see cref="ScheduleCheck.Key"/>), for Week's outlines.
/// </summary>
public sealed record ScheduleProblem(DateTimeOffset Start, DateTimeOffset End, bool Overlap, string Text, string First = "", string Second = "");

/// <summary>
/// The clash and short-gap check for an event being saved (schedule-gap-warning R2-R4, R7): local only, it never
/// blocks saving.
/// </summary>
public static class ScheduleCheck
{
    /// <summary>
    /// Checks the saved occurrence against <paramref name="others"/>. Only timed, not-Free events count, on
    /// <paramref name="members"/> (null: every collection), leaving out <paramref name="own"/> (the event's own item
    /// ids: its other occurrences and exceptions). <paramref name="minimumGap"/> 0 turns the short-gap note off.
    /// Times in the text are in <paramref name="zone"/>.
    /// </summary>
    public static ScheduleNote? Check(DateTimeOffset start, DateTimeOffset end, bool allDay, ShowAs showAs,
        IEnumerable<OccurrenceView> others, IReadOnlySet<string> own, IReadOnlySet<string>? members, int minimumGap,
        TimeZoneInfo zone, CultureInfo culture)
    {
        if (allDay || showAs == ShowAs.Free)
        {
            return null;
        }
        var counted = others.Where(o => Counts(o)
            && !own.Contains(o.ItemId) && (members is null || members.Contains(o.CollectionId))).ToList();

        var clashes = counted.Where(o => o.StartUtc < end && o.EndUtc > start).OrderBy(o => o.StartUtc).ToList();
        if (clashes.Count > 0)
        {
            var first = clashes[0];
            var more = clashes.Count > 1 ? $" +{clashes.Count - 1} more" : "";
            return new ScheduleNote(true, $"Overlaps {Title(first)} {Time(first.StartUtc, zone, culture)}–{Time(first.EndUtc, zone, culture)}{more}");
        }
        if (minimumGap <= 0)
        {
            return null;
        }
        var limit = TimeSpan.FromMinutes(minimumGap);
        var before = counted.Where(o => o.EndUtc <= start).MaxBy(o => o.EndUtc);
        var after = counted.Where(o => o.StartUtc >= end).MinBy(o => o.StartUtc);
        var gapBefore = before is null ? TimeSpan.MaxValue : start - before.EndUtc;
        var gapAfter = after is null ? TimeSpan.MaxValue : after.StartUtc - end;
        var (gap, neighbour, isBefore) = gapBefore <= gapAfter ? (gapBefore, before, true) : (gapAfter, after, false);
        if (neighbour is null || gap >= limit)
        {
            return null;
        }
        if (gap == TimeSpan.Zero)
        {
            return new ScheduleNote(false, $"{(isBefore ? "Right after" : "Right before")} {Title(neighbour)}");
        }
        var minutes = (int)Math.Ceiling(gap.TotalMinutes);
        return new ScheduleNote(false,
            $"{minutes} min {(isBefore ? "after" : "before")} {Title(neighbour)}, less than your {minimumGap} min gap");
    }

    /// <summary>
    /// Every overlap between two counted events, and every short gap between an event and the next one to start
    /// after everything before it has ended (schedule-gap-warning R11). Counted as in <see cref="Check"/>: timed, not
    /// Free, not declined, events only. <paramref name="minimumGap"/> 0 finds overlaps only.
    /// </summary>
    public static IReadOnlyList<ScheduleProblem> Problems(IEnumerable<OccurrenceView> events, int minimumGap, TimeZoneInfo zone,
        CultureInfo culture)
    {
        var counted = events.Where(Counts).OrderBy(o => o.StartUtc).ThenBy(o => o.EndUtc).ToList();
        var problems = new List<ScheduleProblem>();
        for (var i = 0; i < counted.Count; i++)
        {
            var a = counted[i];
            for (var j = i + 1; j < counted.Count && counted[j].StartUtc < a.EndUtc; j++)
            {
                var b = counted[j];
                if (b.EndUtc <= b.StartUtc || a.EndUtc <= a.StartUtc)
                {
                    continue;
                }
                var end = b.EndUtc < a.EndUtc ? b.EndUtc : a.EndUtc;
                problems.Add(new ScheduleProblem(b.StartUtc, end, true,
                    $"{Title(a)} overlaps {Title(b)} {Time(b.StartUtc, zone, culture)}–{Time(end, zone, culture)}", Key(a), Key(b)));
            }
        }
        if (minimumGap > 0)
        {
            var limit = TimeSpan.FromMinutes(minimumGap);
            OccurrenceView? reach = null;
            foreach (var e in counted)
            {
                if (reach is not null && e.StartUtc >= reach.EndUtc && e.StartUtc - reach.EndUtc < limit)
                {
                    var gap = e.StartUtc - reach.EndUtc;
                    problems.Add(new ScheduleProblem(reach.EndUtc, e.StartUtc, false, gap == TimeSpan.Zero
                        ? $"{Title(e)} right after {Title(reach)}"
                        : $"{(int)Math.Ceiling(gap.TotalMinutes)} min between {Title(reach)} and {Title(e)}, less than your {minimumGap} min gap",
                        Key(reach), Key(e)));
                }
                if (reach is null || e.EndUtc > reach.EndUtc)
                {
                    reach = e;
                }
            }
        }
        return [.. problems.OrderBy(p => p.Start)];
    }

    /// <summary>The Month and Agenda text for a day's problems, e.g. "1 overlap, 2 short gaps".</summary>
    public static string Summary(IReadOnlyCollection<ScheduleProblem> problems)
    {
        var overlaps = problems.Count(p => p.Overlap);
        var gaps = problems.Count - overlaps;
        var parts = new List<string>();
        if (overlaps > 0)
        {
            parts.Add(overlaps == 1 ? "1 overlap" : $"{overlaps} overlaps");
        }
        if (gaps > 0)
        {
            parts.Add(gaps == 1 ? "1 short gap" : $"{gaps} short gaps");
        }
        return string.Join(", ", parts);
    }

    /// <summary>An occurrence's key: its item and recurrence, as the views key their cards.</summary>
    public static string Key(OccurrenceView o) => $"{o.ItemId}|{o.RecurrenceUtc?.UtcTicks}";

    private static bool Counts(OccurrenceView o) => o.Kind == ItemKind.Event && !o.AllDay && o.ShowAs != ShowAs.Free && !o.Inactive;

    private static string Title(OccurrenceView o) => string.IsNullOrWhiteSpace(o.Summary) ? "(No title)" : o.Summary;

    private static string Time(DateTimeOffset utc, TimeZoneInfo zone, CultureInfo culture) =>
        TimeZoneInfo.ConvertTime(utc, zone).ToString("t", culture);
}
