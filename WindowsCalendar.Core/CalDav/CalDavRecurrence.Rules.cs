using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.CalDav;

public enum RepeatFrequency { None, Daily, Weekly, Monthly, Yearly }

/// <summary>
/// The editor's repeat controls (recurring-events R3 interim). <see cref="Weekdays"/> are for Weekly.
/// <see cref="MonthlyWeek"/> is for Monthly: null repeats on the start's day of the month, 1–4 or -1 (last) on the
/// start's weekday in that week of the month. It ends after <see cref="Count"/> occurrences, on <see cref="Until"/>
/// (inclusive), or never.
/// </summary>
public sealed record RepeatRule(RepeatFrequency Frequency, int Interval, IReadOnlyList<DayOfWeek> Weekdays, int? MonthlyWeek,
    int? Count, DateOnly? Until)
{
    public static readonly RepeatRule None = new(RepeatFrequency.None, 1, [], null, null, null);

    /// <summary>The week of the month <paramref name="day"/> falls in, 1–5.</summary>
    public static int WeekOfMonth(DateOnly day) => (day.Day - 1) / 7 + 1;

    /// <summary>Whether <paramref name="day"/> is the last of its weekday in its month.</summary>
    public static bool IsLastWeek(DateOnly day) => day.Day + 7 > DateTime.DaysInMonth(day.Year, day.Month);

    public bool Equals(RepeatRule? other) =>
        other is not null && Frequency == other.Frequency
        && (Frequency == RepeatFrequency.None
            || (Interval == other.Interval && Weekdays.Order().SequenceEqual(other.Weekdays.Order())
                && MonthlyWeek == other.MonthlyWeek && Count == other.Count && Until == other.Until));

    public override int GetHashCode() => Frequency == RepeatFrequency.None ? 0 : HashCode.Combine(Frequency, Interval, MonthlyWeek, Count, Until);
}

/// <summary>
/// A rule change: the changed item, the new series or single event after a "this and following" change, the open
/// occurrence's id in the item that now holds it (null when it no longer repeats), and how many overrides were dropped.
/// </summary>
public sealed record RuleChange(Item Original, Item? Following, DateTimeOffset? Recurrence, int Lost);

public static partial class CalDavRecurrence
{
    /// <summary>
    /// The event's repeat rule for the editor: <see cref="RepeatRule.None"/> when it does not repeat, null when the
    /// controls can't show its rule (R7).
    /// </summary>
    public static RepeatRule? ReadRule(Item item, TimeZoneInfo zone) => ReadRule(CalDavEvents.Master(Load(item)), zone);

    /// <summary>
    /// R3: gives the event a new repeat rule. With <paramref name="following"/> on a later occurrence the series is split
    /// (R6) and the new series gets the rule; otherwise the whole event does. Overrides and EXDATEs that are no longer
    /// occurrences are dropped; <see cref="RuleChange.Lost"/> counts the overrides. Null when the rule is unchanged.
    /// </summary>
    public static RuleChange? ChangeRule(Item item, DateTimeOffset? recurrence, bool following, RepeatRule rule, Collection collection,
        TimeZoneInfo zone, DateTimeOffset now)
    {
        var calendar = Load(item);
        var master = CalDavEvents.Master(calendar);
        var current = ReadRule(master, zone) ?? throw new EditRefusedException("This repeat pattern can't be changed here.");
        if (current.Equals(rule))
        {
            return null;
        }
        if (following && recurrence is { } r && master.RecurrenceRule is not null)
        {
            var split = Instance(master, r, zone).Period.StartTime;
            if (!IsFirst(master, split))
            {
                var next = Following(item, split, collection, zone, now);
                var nextCalendar = Load(next);
                var nextMaster = CalDavEvents.Master(nextCalendar);
                // An unchanged COUNT keeps counting from the original start.
                if (rule.Count is not null && rule.Count == current.Count && nextMaster.RecurrenceRule!.Count is { } left)
                {
                    rule = rule with { Count = left };
                }
                var (nextKept, lost) = SetRule(nextCalendar, nextMaster, rule, zone);
                Stamp(nextKept, now);
                next = CalDavEvents.ToItem(next.Id, next.CollectionId, next.RemoteId!, null, nextCalendar, zone);
                Truncate(calendar, master, split, zone);
                Stamp(master, now);
                return new RuleChange(CalDavEvents.ToItem(item.Id, item.CollectionId, item.RemoteId!, item.Etag, calendar, zone),
                    next, Kept(nextCalendar, r, zone), lost);
            }
        }
        var preferred = recurrence ?? OccurrenceExpander.ToUtc(master.DtStart!, zone);
        var (kept, lostAll) = SetRule(calendar, master, rule, zone);
        Stamp(kept, now);
        return new RuleChange(CalDavEvents.ToItem(item.Id, item.CollectionId, item.RemoteId!, item.Etag, calendar, zone),
            null, Kept(calendar, preferred, zone), lostAll);
    }

    private static RepeatRule? ReadRule(CalendarEvent master, TimeZoneInfo zone)
    {
        var hasDates = master.RecurrenceDates.GetAllDates().Any();
        if (master.RecurrenceRule is not { } rule)
        {
            return hasDates ? null : RepeatRule.None;
        }
        if (hasDates)
        {
            return null;
        }
        if (rule.ByHour.Count + rule.ByMinute.Count + rule.BySecond.Count + rule.BySetPosition.Count
            + rule.ByYearDay.Count + rule.ByWeekNo.Count > 0)
        {
            return null;
        }
        // The rule's days are in the start's own form (its TZID, UTC or floating).
        var start = DateOnly.FromDateTime(master.DtStart!.Value);
        IReadOnlyList<DayOfWeek> days = [];
        int? week = null;
        RepeatFrequency frequency;
        switch (rule.Frequency)
        {
            case FrequencyType.Daily when rule.ByDay.Count + rule.ByMonthDay.Count + rule.ByMonth.Count == 0:
                frequency = RepeatFrequency.Daily;
                break;
            case FrequencyType.Weekly when rule.ByMonthDay.Count + rule.ByMonth.Count == 0 && rule.ByDay.All(d => d.Offset is null):
                days = rule.ByDay.Count > 0 ? rule.ByDay.Select(d => d.DayOfWeek).Distinct().ToList() : [start.DayOfWeek];
                if (!days.Contains(start.DayOfWeek))
                {
                    return null;
                }
                frequency = RepeatFrequency.Weekly;
                break;
            case FrequencyType.Monthly when rule.ByMonth.Count == 0:
                if (rule.ByDay is [{ Offset: { } n } d] && rule.ByMonthDay.Count == 0 && d.DayOfWeek == start.DayOfWeek
                    && ((n is >= 1 and <= 4 && n == RepeatRule.WeekOfMonth(start)) || (n == -1 && RepeatRule.IsLastWeek(start))))
                {
                    week = n;
                }
                else if (!(rule.ByDay.Count == 0 && (rule.ByMonthDay.Count == 0 || (rule.ByMonthDay is [var md] && md == start.Day))))
                {
                    return null;
                }
                frequency = RepeatFrequency.Monthly;
                break;
            case FrequencyType.Yearly when rule.ByDay.Count == 0
                && (rule.ByMonth.Count == 0 || (rule.ByMonth is [var m] && m == start.Month))
                && (rule.ByMonthDay.Count == 0 || (rule.ByMonthDay is [var ymd] && ymd == start.Day)):
                frequency = RepeatFrequency.Yearly;
                break;
            default:
                return null;
        }
        return new RepeatRule(frequency, Math.Max(1, rule.Interval), days, week, rule.Count,
            rule.Until is { } until ? UntilDate(master, until, zone) : null);
    }

    /// <summary>The last day an occurrence can start on before UNTIL, as the editor shows it.</summary>
    private static DateOnly UntilDate(CalendarEvent master, CalDateTime until, TimeZoneInfo zone)
    {
        if (!until.HasTime || !master.DtStart!.HasTime)
        {
            return DateOnly.FromDateTime(until.Value);
        }
        var wall = master.DtStart.IsFloating ? until.Value : CalDavEvents.Wall(until, zone);
        var startTime = master.DtStart.IsFloating ? master.DtStart.Value.TimeOfDay : CalDavEvents.Wall(master.DtStart, zone).TimeOfDay;
        var day = DateOnly.FromDateTime(wall);
        return wall.TimeOfDay < startTime ? day.AddDays(-1) : day;
    }

    /// <summary>
    /// Writes <paramref name="rule"/> to the master, or makes the event a single one for None. Returns the component to
    /// stamp and the number of overrides dropped.
    /// </summary>
    private static (CalendarEvent Kept, int Lost) SetRule(Calendar calendar, CalendarEvent master, RepeatRule rule, TimeZoneInfo zone)
    {
        if (master.RecurrenceDates.GetAllDates().Any())
        {
            throw new EditRefusedException("This series repeats on listed dates, so its repeat pattern can't be changed here.");
        }
        var overrides = calendar.Events.Count(e => e.RecurrenceIdentifier is not null);
        if (rule.Frequency == RepeatFrequency.None)
        {
            return MakeSingle(calendar, master, overrides);
        }

        var start = DateOnly.FromDateTime(master.DtStart!.Value);
        if (rule.Frequency == RepeatFrequency.Weekly && rule.Weekdays.Count > 0 && !rule.Weekdays.Contains(start.DayOfWeek))
        {
            // The start moves to the first chosen weekday after it, so it is an occurrence.
            var days = Enumerable.Range(1, 6).First(n => rule.Weekdays.Contains(start.AddDays(n).DayOfWeek));
            var length = master.DtEnd is { } end ? end.Value - master.DtStart.Value : (TimeSpan?)null;
            if (master.Duration is not null && length is not null)
            {
                master.Duration = null;
            }
            master.DtStart = Shift(master.DtStart, TimeSpan.FromDays(days));
            if (length is { } l)
            {
                master.DtEnd = Shift(master.DtStart, l);
            }
            start = start.AddDays(days);
        }

        var old = master.RecurrenceRule;
        master.RecurrenceRule = ToRecurrenceRule(rule, master.DtStart, start, old, zone);

        // What is not an occurrence of the new rule goes.
        var exceptions = master.ExceptionDates.GetAllDates().ToList();
        master.ExceptionDates.Clear();
        foreach (var date in exceptions.Where(d => IsOccurrence(master, d, zone)))
        {
            master.ExceptionDates.Add(date);
        }
        KeepOverrides(calendar, id => IsOccurrence(master, id, zone));
        return (master, overrides - calendar.Events.Count(e => e.RecurrenceIdentifier is not null));
    }

    /// <summary>The series as one event at its start; an override of that occurrence takes the master's place.</summary>
    private static (CalendarEvent Kept, int Lost) MakeSingle(Calendar calendar, CalendarEvent master, int overrides)
    {
        var first = calendar.Events.FirstOrDefault(e => e.RecurrenceIdentifier is { } id
            && id.StartTime.Value == master.DtStart!.Value && id.StartTime.TzId == master.DtStart.TzId);
        var kept = first ?? master;
        if (first is not null)
        {
            first.RecurrenceIdentifier = null;
            first.Sequence = Math.Max(first.Sequence, master.Sequence);
        }
        // By reference: Ical.Net's Remove matches by value.
        var children = calendar.Children.Where(c => c is not CalendarEvent || ReferenceEquals(c, kept)).ToList();
        calendar.Children.Clear();
        foreach (var child in children)
        {
            calendar.Children.Add(child);
        }
        kept.RecurrenceRule = null;
        kept.RecurrenceDates.Clear();
        kept.ExceptionDates.Clear();
        return (kept, overrides - (first is null ? 0 : 1));
    }

    private static RecurrenceRule ToRecurrenceRule(RepeatRule rule, CalDateTime dtStart, DateOnly start, RecurrenceRule? old, TimeZoneInfo zone)
    {
        var result = new RecurrenceRule
        {
            Frequency = rule.Frequency switch
            {
                RepeatFrequency.Daily => FrequencyType.Daily,
                RepeatFrequency.Weekly => FrequencyType.Weekly,
                RepeatFrequency.Monthly => FrequencyType.Monthly,
                _ => FrequencyType.Yearly,
            },
            Interval = Math.Max(1, rule.Interval),
        };
        if (old is not null)
        {
            result.FirstDayOfWeek = old.FirstDayOfWeek;
        }
        if (rule.Frequency == RepeatFrequency.Weekly)
        {
            foreach (var day in (rule.Weekdays.Count > 0 ? rule.Weekdays : [start.DayOfWeek]).Distinct().Order())
            {
                result.ByDay.Add(new WeekDay(day));
            }
        }
        else if (rule.Frequency == RepeatFrequency.Monthly)
        {
            if (rule.MonthlyWeek is { } n)
            {
                result.ByDay.Add(new WeekDay(start.DayOfWeek, n));
            }
            else
            {
                result.ByMonthDay.Add(start.Day);
            }
        }
        if (rule.Count is { } count)
        {
            result.Count = Math.Max(1, count);
        }
        else if (rule.Until is { } until)
        {
            // Inclusive of the chosen day, in the start's form (as for a split).
            var last = until.ToDateTime(new TimeOnly(23, 59, 59));
            result.Until = !dtStart.HasTime ? new CalDateTime(until)
                : dtStart.IsFloating ? new CalDateTime(last, null)
                : new CalDateTime(CalDavEvents.ToUtc(last, zone), "UTC");
        }
        return result;
    }

    /// <summary>Whether the series (ignoring EXDATEs) has an occurrence starting at <paramref name="id"/>.</summary>
    private static bool IsOccurrence(CalendarEvent master, CalDateTime id, TimeZoneInfo zone)
    {
        var exceptions = master.ExceptionDates.GetAllDates().ToList();
        master.ExceptionDates.Clear();
        try
        {
            var at = OccurrenceExpander.ToUtc(id, zone);
            return master.GetOccurrences(OccurrenceExpander.ToCal(at.AddDays(-2)))
                .TakeWhileBefore(OccurrenceExpander.ToCal(at.AddDays(2)))
                .Any(o => OccurrenceExpander.ToUtc(o.Period.StartTime, zone) == at);
        }
        finally
        {
            foreach (var date in exceptions)
            {
                master.ExceptionDates.Add(date);
            }
        }
    }

    /// <summary>The open occurrence's id after a rule change: kept if still an occurrence, else the series' start.</summary>
    private static DateTimeOffset? Kept(Calendar calendar, DateTimeOffset preferred, TimeZoneInfo zone)
    {
        var master = CalDavEvents.Master(calendar);
        if (master.RecurrenceRule is null)
        {
            return null;
        }
        var live = master.GetOccurrences(OccurrenceExpander.ToCal(preferred.AddDays(-2)))
            .TakeWhileBefore(OccurrenceExpander.ToCal(preferred.AddDays(2)))
            .Any(o => OccurrenceExpander.ToUtc(o.Period.StartTime, zone) == preferred);
        return live ? preferred : OccurrenceExpander.ToUtc(master.DtStart!, zone);
    }
}
