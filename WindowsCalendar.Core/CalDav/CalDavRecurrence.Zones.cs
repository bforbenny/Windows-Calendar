using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.CalDav;

public static partial class CalDavRecurrence
{
    /// <summary>How many overrides an edit dropped, for the "changed occurrences will be lost" question.</summary>
    public static int LostOverrides(Item before, Item after) => Overrides(Load(before)) - Overrides(Load(after));

    private static int Overrides(Calendar calendar) => calendar.Events.Count(e => e.RecurrenceIdentifier is not null);

    /// <summary>
    /// R5 with a new timezone (timezone-handling R7): the series moves into the zones of <paramref name="fields"/>,
    /// keeping its wall-clock times (moved by the occurrence's own wall-clock change), so the same occurrences
    /// remain. EXDATEs, RECURRENCE-IDs and UNTIL are converted; overrides whose times were not changed move too, the
    /// others keep their times. EXDATEs and overrides that are no longer occurrences are dropped.
    /// </summary>
    private static void ChangeZone(Calendar calendar, CalendarEvent master, EventFields old, EventFields fields, TimeZoneInfo zone)
    {
        if (old.AllDay)
        {
            throw new EditRefusedException("A whole series can't switch between all-day and timed yet. Choose This event instead.");
        }
        if (master.RecurrenceDates.GetAllDates().Any())
        {
            throw new EditRefusedException("This series repeats on listed dates, so its timezone can't be changed as a whole.");
        }
        var move = new ZoneMove(calendar, master, old, fields, zone);
        if (master.RecurrenceRule is { } rule)
        {
            var until = rule.Until;
            rule.Until = null;
            ShiftRule(rule, DateOnly.FromDateTime(move.From), DateOnly.FromDateTime(fields.Start), move.Delta);
            rule.Until = until is null ? null : move.Until(until);
        }

        if (master.Duration is not null)
        {
            master.Duration = null;
        }
        master.DtStart = move.Move(master.DtStart!);
        master.DtEnd = move.End(master.DtStart);

        var exceptions = master.ExceptionDates.GetAllDates().ToList();
        master.ExceptionDates.Clear();
        foreach (var date in exceptions)
        {
            master.ExceptionDates.Add(move.Move(date));
        }
        foreach (var ov in calendar.Events.Where(e => e.RecurrenceIdentifier is not null).ToList())
        {
            var id = ov.RecurrenceIdentifier!.StartTime;
            var untouched = ov.DtStart is { } s && s.Value == id.Value && s.TzId == id.TzId && s.HasTime == id.HasTime;
            ov.RecurrenceIdentifier = new RecurrenceIdentifier(move.Move(id));
            if (untouched)
            {
                if (ov.Duration is not null)
                {
                    ov.Duration = null;
                }
                ov.DtStart = move.Move(id);
                ov.DtEnd = move.End(ov.DtStart);
            }
        }

        // A wall-clock time the new zone skips (a DST change) is no longer an occurrence.
        var kept = master.ExceptionDates.GetAllDates().ToList().Where(d => IsOccurrence(master, d, zone)).ToList();
        master.ExceptionDates.Clear();
        foreach (var date in kept)
        {
            master.ExceptionDates.Add(date);
        }
        KeepOverrides(calendar, id => IsOccurrence(master, id, zone));
    }

    /// <summary>
    /// A series' date-times moved into new zones (timezone-handling R7): each is read as a wall-clock time in the
    /// series' old form (its TZID, UTC, or floating in the Windows timezone), moved by <see cref="Delta"/>, and written
    /// in the new start zone; a UTC value stays UTC.
    /// </summary>
    private sealed class ZoneMove
    {
        private readonly Calendar _calendar;
        private readonly CalDateTime _form;
        private readonly TimeZoneInfo _windows;
        private readonly EventFields _fields;
        private readonly TimeSpan _length;

        public ZoneMove(Calendar calendar, CalendarEvent master, EventFields old, EventFields fields, TimeZoneInfo zone)
        {
            _calendar = calendar;
            _form = master.DtStart!;
            _windows = zone;
            _fields = fields;
            From = Own(old.Start, _form, zone);
            Delta = fields.Start - From;
            _length = CalDavEvents.ToUtc(fields.End, fields.EffectiveEndZone!) - CalDavEvents.ToUtc(fields.Start, fields.StartZone!);
        }

        /// <summary>The occurrence's old start as a wall-clock time in the series' old form.</summary>
        public DateTime From { get; }

        /// <summary>The wall-clock change, old form to new zone.</summary>
        public TimeSpan Delta { get; }

        public CalDateTime Move(CalDateTime t)
        {
            if (!t.HasTime)
            {
                return Shift(t, Delta);
            }
            var utc = CalDavEvents.ToUtc(OldWall(t) + Delta, _fields.StartZone!);
            return t.IsUtc && !_form.IsUtc ? new CalDateTime(utc, "UTC") : CalDavEvents.InZone(_calendar, utc, _fields.StartZone!);
        }

        /// <summary>UNTIL, as UTC (the series now has a TZID).</summary>
        public CalDateTime Until(CalDateTime until) =>
            until.HasTime ? new CalDateTime(CalDavEvents.ToUtc(OldWall(until) + Delta, _fields.StartZone!), "UTC") : Shift(until, Delta);

        /// <summary>The end of an occurrence starting at <paramref name="start"/>, in the new end zone.</summary>
        public CalDateTime End(CalDateTime start) =>
            CalDavEvents.InZone(_calendar, DateTime.SpecifyKind(start.AsUtc + _length, DateTimeKind.Utc), _fields.EffectiveEndZone!);

        private DateTime OldWall(CalDateTime t)
        {
            if (t.IsFloating == _form.IsFloating && t.TzId == _form.TzId)
            {
                return t.Value;
            }
            var instant = OccurrenceExpander.ToUtc(t, _windows);
            return _form.IsFloating ? TimeZoneInfo.ConvertTime(instant, _windows).DateTime : CalDavEvents.InZone(instant.UtcDateTime, _form.TzId!).Value;
        }
    }
}
