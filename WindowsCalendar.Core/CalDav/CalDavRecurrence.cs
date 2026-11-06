using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using WindowsCalendar.Core.Store;
using CultureInfo = System.Globalization.CultureInfo;

namespace WindowsCalendar.Core.CalDav;

/// <summary>A change that is not made, with the reason to show the user.</summary>
public sealed class EditRefusedException(string message) : Exception(message);

/// <summary>
/// Edits of recurring CalDAV events (recurring-events interim: R4 this occurrence, R5 entire series). An occurrence is
/// named by its original start in UTC (its RECURRENCE-ID), as stored in the occurrence index. Edits keep everything
/// else in the payload, as <see cref="CalDavEvents"/> does.
/// </summary>
public static class CalDavRecurrence
{
    /// <summary>The fields of one occurrence: its override's, or the series' at that occurrence's times.</summary>
    public static EventFields ReadOccurrence(Item item, DateTimeOffset recurrence, TimeZoneInfo zone) =>
        ReadOccurrence(Load(item), recurrence, zone);

    /// <summary>R4: adds or updates the occurrence's override. Null when nothing changed.</summary>
    public static Item? EditOccurrence(Item item, DateTimeOffset recurrence, EventFields fields, TimeZoneInfo zone, DateTimeOffset now)
    {
        var calendar = Load(item);
        var master = CalDavEvents.Master(calendar);
        var old = ReadOccurrence(calendar, recurrence, zone);
        var (timesChanged, textChanged) = Diff(old, fields);
        if (!timesChanged && !textChanged)
        {
            return null;
        }
        if (timesChanged)
        {
            CheckNeighbours(calendar, recurrence, fields, zone);
        }

        if (Override(calendar, recurrence, zone) is not { } ov)
        {
            ov = master.Copy<CalendarEvent>()!;
            ov.RecurrenceRule = null;
            ov.RecurrenceDates.Clear();
            ov.ExceptionDates.Clear();
            ov.RecurrenceIdentifier = new RecurrenceIdentifier(Instance(master, recurrence, zone).Period.StartTime);
            calendar.Events.Add(ov);
            CalDavEvents.SetTimes(calendar, ov, old, zone);
        }
        if (timesChanged)
        {
            CalDavEvents.SetTimes(calendar, ov, fields, zone);
        }
        if (textChanged)
        {
            CalDavEvents.SetText(ov, fields);
        }
        Stamp(ov, now);
        return CalDavEvents.ToItem(item.Id, item.CollectionId, item.RemoteId!, item.Etag, calendar, zone);
    }

    /// <summary>
    /// R5: changes the whole series from one occurrence's new fields. Text goes to the master. A time change moves the
    /// series by the same amount; a move to another day shifts the rule (see <see cref="ShiftRule"/>). Null when nothing
    /// changed.
    /// </summary>
    public static Item? EditSeries(Item item, DateTimeOffset recurrence, EventFields fields, TimeZoneInfo zone, DateTimeOffset now)
    {
        var calendar = Load(item);
        var master = CalDavEvents.Master(calendar);
        var old = ReadOccurrence(calendar, recurrence, zone);
        var (timesChanged, textChanged) = Diff(old, fields);
        if (!timesChanged && !textChanged)
        {
            return null;
        }
        if (textChanged)
        {
            if (old.Title != fields.Title)
            {
                master.Summary = fields.Title;
            }
            if (CalDavEvents.Blank(old.Location) != CalDavEvents.Blank(fields.Location))
            {
                master.Location = CalDavEvents.Blank(fields.Location);
            }
            if (CalDavEvents.Blank(old.Description) != CalDavEvents.Blank(fields.Description))
            {
                master.Description = CalDavEvents.Blank(fields.Description);
            }
        }
        if (timesChanged)
        {
            MoveSeries(calendar, master, old, fields);
        }
        Stamp(master, now);
        return CalDavEvents.ToItem(item.Id, item.CollectionId, item.RemoteId!, item.Etag, calendar, zone);
    }

    /// <summary>Whether saving <paramref name="fields"/> over <paramref name="old"/> would change anything.</summary>
    public static bool Changed(EventFields old, EventFields fields) => Diff(old, fields) is (true, _) or (_, true);

    /// <summary>
    /// The occurrence's id once <see cref="EditSeries"/> has moved the series to <paramref name="fields"/>' times
    /// (its RECURRENCE-ID moves with the series). Call with the item before the edit.
    /// </summary>
    public static DateTimeOffset MovedRecurrence(Item item, DateTimeOffset recurrence, EventFields fields, TimeZoneInfo zone)
    {
        var calendar = Load(item);
        var old = ReadOccurrence(calendar, recurrence, zone);
        if (old.Start == fields.Start)
        {
            return recurrence;
        }
        var id = Override(calendar, recurrence, zone)?.RecurrenceIdentifier!.StartTime
            ?? Instance(CalDavEvents.Master(calendar), recurrence, zone).Period.StartTime;
        return OccurrenceExpander.ToUtc(Shift(id, fields.Start - old.Start), zone);
    }

    /// <summary>R4 delete: an EXDATE for the occurrence, and its override (if any) removed.</summary>
    public static Item DeleteOccurrence(Item item, DateTimeOffset recurrence, TimeZoneInfo zone, DateTimeOffset now)
    {
        var calendar = Load(item);
        var master = CalDavEvents.Master(calendar);
        CalDateTime id;
        if (Override(calendar, recurrence, zone) is { } ov)
        {
            id = ov.RecurrenceIdentifier!.StartTime;
            // By reference: Ical.Net's Remove matches by value, and an override can equal its master.
            var keep = calendar.Children.Where(c => !ReferenceEquals(c, ov)).ToList();
            calendar.Children.Clear();
            foreach (var child in keep)
            {
                calendar.Children.Add(child);
            }
        }
        else
        {
            id = Instance(master, recurrence, zone).Period.StartTime;
        }
        master.ExceptionDates.Add(id);
        Stamp(master, now);
        return CalDavEvents.ToItem(item.Id, item.CollectionId, item.RemoteId!, item.Etag, calendar, zone);
    }

    /// <summary>The rule in words for the editor, e.g. "Repeats weekly on Monday"; null when not recurring.</summary>
    public static string? Describe(Item item)
    {
        var master = CalDavEvents.Master(Load(item));
        if (master.RecurrenceRule is not { } rule)
        {
            return master.RecurrenceDates.GetAllDates().Any() ? "Repeats on set dates" : null;
        }
        var unit = rule.Frequency switch
        {
            FrequencyType.Daily => "day",
            FrequencyType.Weekly => "week",
            FrequencyType.Monthly => "month",
            FrequencyType.Yearly => "year",
            _ => null,
        };
        if (unit is null)
        {
            return "Repeats";
        }
        var text = rule.Interval > 1 ? $"Repeats every {rule.Interval} {unit}s"
            : $"Repeats {(unit == "day" ? "daily" : unit + "ly")}";
        var names = CultureInfo.CurrentCulture.DateTimeFormat;
        if (rule.ByDay.Count > 0)
        {
            text += " on " + string.Join(", ", rule.ByDay.Select(d => d.Offset is { } n
                ? $"the {Ordinal(n)} {names.GetDayName(d.DayOfWeek)}"
                : names.GetDayName(d.DayOfWeek)));
        }
        else if (rule.ByMonthDay.Count > 0)
        {
            text += " on day " + string.Join(", ", rule.ByMonthDay);
        }
        if (rule.Count is { } count)
        {
            text += $", {count} times";
        }
        else if (rule.Until is { } until)
        {
            text += $", until {until.Value:d}";
        }
        return text;
    }

    // ---- Occurrences ----

    private static Calendar Load(Item item) => Calendar.Load(item.RawPayload)!;

    private static CalendarEvent? Override(Calendar calendar, DateTimeOffset recurrence, TimeZoneInfo zone) =>
        calendar.Events.FirstOrDefault(e => e.RecurrenceIdentifier is { } id && OccurrenceExpander.ToUtc(id.StartTime, zone) == recurrence);

    /// <summary>The series' own instance at the original start, whether or not it is overridden.</summary>
    private static Occurrence Instance(CalendarEvent master, DateTimeOffset recurrence, TimeZoneInfo zone) =>
        master.GetOccurrences(OccurrenceExpander.ToCal(recurrence.AddDays(-2)))
            .TakeWhileBefore(OccurrenceExpander.ToCal(recurrence.AddDays(2)))
            .FirstOrDefault(o => OccurrenceExpander.ToUtc(o.Period.StartTime, zone) == recurrence)
        ?? throw new EditRefusedException("This occurrence no longer exists.");

    private static EventFields ReadOccurrence(Calendar calendar, DateTimeOffset recurrence, TimeZoneInfo zone)
    {
        if (Override(calendar, recurrence, zone) is { } ov)
        {
            return CalDavEvents.Read(ov, zone);
        }
        var master = CalDavEvents.Master(calendar);
        var fields = CalDavEvents.Read(master, zone);
        var period = Instance(master, recurrence, zone).Period;
        var end = period.EffectiveEndTime ?? period.StartTime;
        return fields.AllDay
            ? fields with { Start = period.StartTime.Value.Date, End = end.Value.Date }
            : fields with { Start = CalDavEvents.Wall(period.StartTime, zone), End = CalDavEvents.Wall(end, zone) };
    }

    private static (bool Times, bool Text) Diff(EventFields old, EventFields fields) =>
        (old.AllDay != fields.AllDay || old.Start != fields.Start || old.End != fields.End,
         old.Title != fields.Title || CalDavEvents.Blank(old.Location) != CalDavEvents.Blank(fields.Location)
            || CalDavEvents.Blank(old.Description) != CalDavEvents.Blank(fields.Description));

    /// <summary>R4: an occurrence may not move onto or past the previous or next one.</summary>
    private static void CheckNeighbours(Calendar calendar, DateTimeOffset recurrence, EventFields fields, TimeZoneInfo zone)
    {
        var all = OccurrenceExpander.Expand(calendar, recurrence.AddDays(-400), recurrence.AddDays(400), zone)
            .OrderBy(o => o.Start).ToList();
        var index = all.FindIndex(o => o.Recurrence == recurrence);
        if (index < 0)
        {
            return;
        }
        var start = CalDavEvents.ToUtc(fields.Start, zone);
        if ((index > 0 && start <= all[index - 1].Start) || (index < all.Count - 1 && start >= all[index + 1].Start))
        {
            throw new EditRefusedException("An occurrence can't move onto or past the previous or next one in its series.");
        }
    }

    private static void Stamp(CalendarEvent ev, DateTimeOffset now)
    {
        ev.Sequence += 1;
        ev.DtStamp = CalDavEvents.Utc(now);
        ev.LastModified = CalDavEvents.Utc(now);
    }

    // ---- Moving a series (R5) ----

    /// <summary>
    /// Moves the master by the occurrence's change in start and gives it the new length. EXDATEs, override
    /// RECURRENCE-IDs and the rule's UNTIL move with it; overrides whose times were not changed move too.
    /// </summary>
    private static void MoveSeries(Calendar calendar, CalendarEvent master, EventFields old, EventFields fields)
    {
        if (old.AllDay != fields.AllDay)
        {
            throw new EditRefusedException("A whole series can't switch between all-day and timed yet. Choose This event instead.");
        }
        if (master.RecurrenceDates.GetAllDates().Any())
        {
            throw new EditRefusedException("This series repeats on listed dates, so it can't be moved as a whole. Choose This event instead.");
        }
        var delta = fields.Start - old.Start;
        var length = fields.End - fields.Start;
        if (master.RecurrenceRule is { } rule)
        {
            ShiftRule(rule, DateOnly.FromDateTime(old.Start), DateOnly.FromDateTime(fields.Start), delta);
        }

        if (master.Duration is not null)
        {
            master.Duration = null;
        }
        master.DtStart = Shift(master.DtStart!, delta);
        master.DtEnd = Shift(master.DtStart, length);

        var exceptions = master.ExceptionDates.GetAllDates().ToList();
        master.ExceptionDates.Clear();
        foreach (var date in exceptions)
        {
            master.ExceptionDates.Add(Shift(date, delta));
        }

        foreach (var ov in calendar.Events.Where(e => e.RecurrenceIdentifier is not null).ToList())
        {
            var id = ov.RecurrenceIdentifier!.StartTime;
            var untouched = ov.DtStart is { } s && s.Value == id.Value && s.TzId == id.TzId && s.HasTime == id.HasTime;
            ov.RecurrenceIdentifier = new RecurrenceIdentifier(Shift(id, delta));
            if (untouched)
            {
                if (ov.Duration is not null)
                {
                    ov.Duration = null;
                }
                ov.DtStart = Shift(id, delta);
                ov.DtEnd = Shift(ov.DtStart, length);
            }
        }
    }

    /// <summary>
    /// Keeps a rule matching a series moved from <paramref name="from"/> to <paramref name="to"/>: weekdays shift by
    /// the same number of days ("2nd Tuesday" becomes "2nd Wednesday"), a single month day shifts if it stays within
    /// 1–28, and UNTIL moves. Rules that cannot follow are refused.
    /// </summary>
    internal static void ShiftRule(RecurrenceRule rule, DateOnly from, DateOnly to, TimeSpan delta)
    {
        const string refused = "This series' repeat pattern can't follow that move. Choose This event, or change the time only.";
        if (delta.Ticks % TimeSpan.TicksPerDay != 0 && (rule.ByHour.Count > 0 || rule.ByMinute.Count > 0 || rule.BySecond.Count > 0))
        {
            throw new EditRefusedException(refused);
        }
        var days = to.DayNumber - from.DayNumber;
        if (days != 0)
        {
            if (rule.BySetPosition.Count > 0 || rule.ByYearDay.Count > 0 || rule.ByWeekNo.Count > 0
                || (rule.ByMonth.Count > 0 && from.Month != to.Month))
            {
                throw new EditRefusedException(refused);
            }
            if (rule.ByMonthDay.Count > 0)
            {
                var day = rule.ByMonthDay[0] + days;
                if (rule.ByMonthDay.Count > 1 || rule.ByMonthDay[0] < 1 || day < 1 || day > 28)
                {
                    throw new EditRefusedException(refused);
                }
                rule.ByMonthDay[0] = day;
            }
            for (var i = 0; i < rule.ByDay.Count; i++)
            {
                var d = rule.ByDay[i];
                var shifted = (DayOfWeek)((((int)d.DayOfWeek + days) % 7 + 7) % 7);
                rule.ByDay[i] = d.Offset is { } n ? new WeekDay(shifted, n) : new WeekDay(shifted);
            }
        }
        if (rule.Until is { } until)
        {
            rule.Until = Shift(until, delta);
        }
    }

    /// <summary>A date-time moved by a wall-clock amount in its own form (TZID, floating, UTC); a date by whole days.</summary>
    private static CalDateTime Shift(CalDateTime t, TimeSpan delta) =>
        t.HasTime
            ? new CalDateTime(t.Value + delta, t.TzId)
            : new CalDateTime(DateOnly.FromDateTime(t.Value).AddDays((int)Math.Round(delta.TotalDays)));

    private static string Ordinal(int n) => n switch
    {
        -1 => "last",
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => $"{n}th",
    };
}
