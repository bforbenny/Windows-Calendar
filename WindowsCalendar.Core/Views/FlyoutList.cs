using System.Globalization;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Views;

/// <summary>
/// What the tray flyout lists (tray-flyout R3, R3a). Each event appears once, in the first section it qualifies for.
/// <paramref name="Next"/> holds the next timed events (several when they start together), null when none is left today
/// or tomorrow; <paramref name="NextIsTomorrow"/> says which day they start on.
/// </summary>
public sealed record FlyoutContent(
    string? Holiday,
    IReadOnlyList<OccurrenceView> AllDay,
    IReadOnlyList<OccurrenceView> InProgress,
    IReadOnlyList<OccurrenceView> LaterToday,
    IReadOnlyList<OccurrenceView> Tomorrow,
    IReadOnlyList<Item> Tasks,
    IReadOnlyList<OccurrenceView> Next,
    bool NextIsTomorrow);

/// <summary>Builds the tray flyout's list from occurrences and tasks (tray-flyout R3a).</summary>
public static class FlyoutList
{
    /// <summary>
    /// <paramref name="occurrences"/> should cover today and tomorrow; events that already ended are left out.
    /// All-day events, and timed ones of 24 hours or more, go in the all-day row (as in the views). The holiday calendar's
    /// all-day events name the day instead of being rows. Tasks: open ones due today or earlier.
    /// </summary>
    public static FlyoutContent Build(IEnumerable<OccurrenceView> occurrences, IEnumerable<Item> tasks, string? holidayCollectionId,
        DateTimeOffset now, TimeZoneInfo zone)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var today = localNow.Date;
        var tomorrow = today.AddDays(1);
        var dayAfter = today.AddDays(2);
        DateTime Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, zone).DateTime;

        var holidays = new List<string>();
        var allDay = new List<OccurrenceView>();
        var inProgress = new List<OccurrenceView>();
        var later = new List<OccurrenceView>();
        var tomorrowAllDay = new List<OccurrenceView>();
        var tomorrowTimed = new List<OccurrenceView>();

        foreach (var o in occurrences.Where(o => o.Kind == ItemKind.Event)
            .OrderBy(o => o.StartUtc).ThenBy(o => o.Summary, StringComparer.CurrentCultureIgnoreCase))
        {
            var start = Local(o.StartUtc);
            var end = Local(o.EndUtc);
            var wholeDay = o.AllDay || end - start >= TimeSpan.FromHours(24);
            if (wholeDay)
            {
                var onToday = start < tomorrow && end > today;
                var onTomorrow = !onToday && start < dayAfter && end > tomorrow;
                if (o.AllDay && o.CollectionId == holidayCollectionId)
                {
                    if (onToday)
                    {
                        holidays.Add(Title(o));
                    }
                }
                else if (onToday)
                {
                    allDay.Add(o);
                }
                else if (onTomorrow)
                {
                    tomorrowAllDay.Add(o);
                }
            }
            else if (start <= localNow && end > localNow)
            {
                inProgress.Add(o);
            }
            else if (start > localNow && start < tomorrow)
            {
                later.Add(o);
            }
            else if (start >= tomorrow && start < dayAfter)
            {
                tomorrowTimed.Add(o);
            }
        }

        var todayDate = DateOnly.FromDateTime(today);
        var due = tasks
            .Where(t => !TaskQuery.IsCompleted(t) && TaskQuery.DueDay(t, zone) is { } d && d <= todayDate)
            .OrderBy(t => TaskQuery.DueDay(t, zone))
            .ThenBy(t => t.Summary, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // The next timed events: later today, else the first ones tomorrow; all that share the earliest start.
        var nextIsTomorrow = later.Count == 0;
        var pool = nextIsTomorrow ? tomorrowTimed : later;
        var next = pool.Count == 0 ? [] : pool.Where(o => o.StartUtc == pool[0].StartUtc).ToList();

        return new FlyoutContent(holidays.Count == 0 ? null : string.Join(", ", holidays), allDay, inProgress, later,
            [.. tomorrowAllDay, .. tomorrowTimed], due, next, nextIsTomorrow && next.Count > 0);
    }

    /// <summary>
    /// The "Next" line (tray-flyout R3, R4a): "Next: Standup in 12 min", "Next: Standup and Review in 1 h 5 min",
    /// "Next: tomorrow 09:00 Standup", or "Nothing else today".
    /// </summary>
    public static string NextText(FlyoutContent content, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        if (content.Next.Count == 0)
        {
            return "Nothing else today";
        }
        var names = content.Next.Count switch
        {
            1 => Title(content.Next[0]),
            2 => $"{Title(content.Next[0])} and {Title(content.Next[1])}",
            _ => $"{Title(content.Next[0])} and {content.Next.Count - 1} more",
        };
        var start = content.Next[0].StartUtc;
        if (content.NextIsTomorrow)
        {
            return $"Next: tomorrow {TimeZoneInfo.ConvertTime(start, zone).ToString("t", culture)} {names}";
        }
        var minutes = (int)Math.Ceiling((start - now).TotalMinutes);
        var wait = minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
        return $"Next: {names} in {wait}";
    }

    public static string Title(OccurrenceView o) => string.IsNullOrWhiteSpace(o.Summary) ? "(No title)" : o.Summary;
}
