using System.Globalization;
using System.Text.Json.Nodes;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Graph;

/// <summary>
/// A Graph series change: <see cref="Updated"/> is PATCHed (the master, or a single event that starts repeating);
/// <see cref="Created"/> is POSTed after it (the new series or single event of a "this and following" change, in one
/// linked group), or replaces the master when <see cref="ReplacesMaster"/> (Doesn't repeat for all events: a create,
/// then the master's delete). <see cref="Lost"/> counts the stored exceptions Exchange will drop.
/// </summary>
public sealed record GraphSeriesChange(Item? Updated, Item? Created, bool ReplacesMaster, int Lost);

/// <summary>
/// Recurring MS365 events (recurring-events MS365 interim). Graph keeps a series master with a <c>recurrence</c>
/// pattern, and each occurrence and exception is its own event. This occurrence edits go to the occurrence
/// (<see cref="GraphEvents.Edit"/>); entire-series, this-and-following and rule changes go to the master here.
/// Dates of a pattern are in the event's own timezone.
/// </summary>
public static class GraphRecurrence
{
    private static readonly string[] Ordinals = ["first", "second", "third", "fourth"];

    // ---- Rule mapping (R3) ----

    /// <summary>The master's rule for the Repeat panel: None when it does not repeat, null when the panel can't show it (R7).</summary>
    public static RepeatRule? ReadRule(Item master, TimeZoneInfo zone)
    {
        var ev = GraphEvents.Parse(master.RawPayload);
        return ev["recurrence"] is JsonObject recurrence ? ReadRule(recurrence, StartDate(master, ev, zone)) : RepeatRule.None;
    }

    private static RepeatRule? ReadRule(JsonObject recurrence, DateOnly start)
    {
        if (recurrence["pattern"] is not JsonObject p || recurrence["range"] is not JsonObject range)
        {
            return null;
        }
        var interval = Int(p["interval"]) ?? 1;
        var days = (p["daysOfWeek"] as JsonArray ?? []).Select(d => Day(Str(d))).ToList();
        if (interval < 1 || days.Any(d => d is null))
        {
            return null;
        }
        var weekdays = days.Select(d => d!.Value).Distinct().ToList();
        RepeatRule? rule = Str(p["type"]) switch
        {
            "daily" => new RepeatRule(RepeatFrequency.Daily, interval, [], null, null, null),
            "weekly" when weekdays.Count > 0 => new RepeatRule(RepeatFrequency.Weekly, interval, weekdays, null, null, null),
            "absoluteMonthly" when Int(p["dayOfMonth"]) == start.Day => new RepeatRule(RepeatFrequency.Monthly, interval, [], null, null, null),
            "relativeMonthly" when weekdays.Count == 1 && weekdays[0] == start.DayOfWeek && Week(Str(p["index"]), start) is { } week =>
                new RepeatRule(RepeatFrequency.Monthly, interval, [], week, null, null),
            "absoluteYearly" when Int(p["dayOfMonth"]) == start.Day && Int(p["month"]) == start.Month =>
                new RepeatRule(RepeatFrequency.Yearly, interval, [], null, null, null),
            _ => null,
        };
        return Str(range["type"]) switch
        {
            "noEnd" => rule,
            "numbered" when Int(range["numberOfOccurrences"]) is > 0 and var n => rule is null ? null : rule with { Count = n },
            "endDate" when DateOnly.TryParse(Str(range["endDate"]), CultureInfo.InvariantCulture, out var until) =>
                rule is null ? null : rule with { Until = until },
            _ => null,
        };
    }

    /// <summary>The <c>recurrence</c> for <paramref name="rule"/> starting on <paramref name="start"/>.</summary>
    internal static JsonObject Recurrence(RepeatRule rule, DateOnly start, TimeZoneInfo zone)
    {
        var p = new JsonObject { ["interval"] = rule.Interval };
        switch (rule.Frequency)
        {
            case RepeatFrequency.Daily:
                p["type"] = "daily";
                break;
            case RepeatFrequency.Weekly:
                p["type"] = "weekly";
                p["daysOfWeek"] = Days(rule.Weekdays.Count > 0 ? rule.Weekdays : [start.DayOfWeek]);
                p["firstDayOfWeek"] = "sunday";
                break;
            case RepeatFrequency.Monthly when rule.MonthlyWeek is { } week:
                p["type"] = "relativeMonthly";
                p["daysOfWeek"] = Days([start.DayOfWeek]);
                p["index"] = week is >= 1 and <= 4 ? Ordinals[week - 1] : "last";
                break;
            case RepeatFrequency.Monthly:
                p["type"] = "absoluteMonthly";
                p["dayOfMonth"] = start.Day;
                break;
            case RepeatFrequency.Yearly:
                p["type"] = "absoluteYearly";
                p["dayOfMonth"] = start.Day;
                p["month"] = start.Month;
                break;
            default:
                throw new ArgumentException("A rule that repeats is needed.", nameof(rule));
        }
        var range = new JsonObject { ["startDate"] = Iso(start), ["recurrenceTimeZone"] = zone.Id };
        if (rule.Count is { } count)
        {
            range["type"] = "numbered";
            range["numberOfOccurrences"] = count;
        }
        else if (rule.Until is { } until)
        {
            range["type"] = "endDate";
            range["endDate"] = Iso(until);
        }
        else
        {
            range["type"] = "noEnd";
        }
        return new JsonObject { ["pattern"] = p, ["range"] = range };
    }

    /// <summary>
    /// The dates of <paramref name="rule"/>'s pattern from <paramref name="start"/> on, ignoring its end. Months
    /// without the day use their last day, as Exchange does.
    /// </summary>
    internal static IEnumerable<DateOnly> Dates(RepeatRule rule, DateOnly start)
    {
        for (var k = 0; k < 100_000; k++)
        {
            switch (rule.Frequency)
            {
                case RepeatFrequency.Daily:
                    yield return start.AddDays(k * rule.Interval);
                    break;
                case RepeatFrequency.Weekly:
                    var weekStart = start.AddDays(-(int)start.DayOfWeek).AddDays(7 * k * rule.Interval);
                    for (var d = 0; d < 7; d++)
                    {
                        var day = weekStart.AddDays(d);
                        if (day >= start && rule.Weekdays.Contains(day.DayOfWeek))
                        {
                            yield return day;
                        }
                    }
                    break;
                case RepeatFrequency.Monthly:
                    var month = new DateOnly(start.Year, start.Month, 1).AddMonths(k * rule.Interval);
                    var date = rule.MonthlyWeek is { } week ? NthWeekday(month, start.DayOfWeek, week)
                        : month.AddDays(Math.Min(start.Day, DateTime.DaysInMonth(month.Year, month.Month)) - 1);
                    if (date >= start)
                    {
                        yield return date;
                    }
                    break;
                case RepeatFrequency.Yearly:
                    var year = start.Year + k * rule.Interval;
                    if (year > 9998)
                    {
                        yield break;
                    }
                    yield return new DateOnly(year, start.Month, Math.Min(start.Day, DateTime.DaysInMonth(year, start.Month)));
                    break;
                default:
                    yield break;
            }
        }
    }

    // ---- Entire series (R5) ----

    /// <summary>
    /// The master with <paramref name="fields"/> applied to the whole series, from the occurrence that started at
    /// <paramref name="recurrence"/>: text fields as they are; a time change moves the master by the same amount, and a
    /// move to another day shifts the pattern (weekly days by the same number of days; the others follow the start).
    /// Null when nothing changed. Refuses switching between all-day and timed, and moving an unreadable pattern's day.
    /// </summary>
    public static Item? EditSeries(Item master, DateTimeOffset recurrence, EventFields fields, TimeZoneInfo zone)
    {
        var first = GraphEventMapper.Read(master, null, zone);
        var old = Occurrence(master, recurrence, zone);
        var edited = GraphEvents.Edit(master, null, fields with { AllDay = first.AllDay, Start = first.Start, End = first.End }, zone);
        if (fields.AllDay == old.AllDay && fields.Start == old.Start && fields.End == old.End)
        {
            return edited;
        }
        if (fields.AllDay != old.AllDay)
        {
            throw new EditRefusedException("A whole series can't switch between all-day and timed. Change this event instead.");
        }
        var ev = GraphEvents.Parse((edited ?? master).RawPayload);
        var changed = GraphEvents.Changed(ev);
        var newStart = first.Start + (fields.Start - old.Start);
        var moved = first with { Start = newStart, End = newStart + (fields.End - fields.Start) };
        var own = GraphEvents.OwnZone(ev, zone);
        GraphEvents.SetTimes(ev, moved, zone, own);
        changed.UnionWith(["start", "end", "isAllDay"]);
        var days = DateOnly.FromDateTime(moved.Start).DayNumber - DateOnly.FromDateTime(first.Start).DayNumber;
        if (ev["recurrence"] is JsonObject recurrenceNode && days != 0)
        {
            var newDate = OwnDate(moved.Start, zone, own, moved.AllDay);
            var rule = ReadRule(recurrenceNode, StartDate(master, GraphEvents.Parse(master.RawPayload), zone))
                ?? throw new EditRefusedException("This repeat pattern can't be moved to another day. Change this event instead.");
            ev["recurrence"] = Recurrence(Shift(rule, days), newDate, RangeZone(recurrenceNode, own));
            changed.Add("recurrence");
        }
        return GraphEvents.WithChanged(master, ev, changed, zone);
    }

    // ---- This and following (R6) ----

    /// <summary>
    /// A split at the occurrence that started at <paramref name="recurrence"/>: the master ends the day before it, and a
    /// new series with <paramref name="fields"/> starts there (null on the first occurrence: the caller acts on the
    /// whole series). A numbered range keeps the occurrences left.
    /// </summary>
    public static (Item Original, Item Following)? SplitSeries(Item master, DateTimeOffset recurrence, EventFields fields, Collection collection,
        TimeZoneInfo zone)
    {
        var ev = GraphEvents.Parse(master.RawPayload);
        var own = GraphEvents.OwnZone(ev, zone);
        var occurrence = Occurrence(master, recurrence, zone);
        var at = OwnDate(occurrence.Start, zone, own, occurrence.AllDay);
        var startDate = StartDate(master, ev, zone);
        if (ev["recurrence"] is not JsonObject recurrenceNode || at <= startDate)
        {
            return null;
        }
        var rule = ReadRule(recurrenceNode, startDate);
        if (rule is null && (Str(recurrenceNode["range"]?["type"]) == "numbered" || Date(fields.Start) != Date(occurrence.Start)))
        {
            throw new EditRefusedException("This repeat pattern can't be split here. Change this event or the whole series instead.");
        }

        var following = GraphEvents.Parse(GraphEvents.MoveTo(master, collection, zone).RawPayload);
        GraphEvents.SetText(following, fields);
        GraphEvents.SetTimes(following, fields, zone, own);
        var newDate = OwnDate(fields.Start, zone, own, fields.AllDay);
        if (rule is not null)
        {
            var left = rule.Count is { } count ? Math.Max(1, count - Dates(rule, startDate).TakeWhile(d => d < at).Count()) : (int?)null;
            var days = Date(fields.Start).DayNumber - Date(occurrence.Start).DayNumber;
            following["recurrence"] = Recurrence(Shift(rule, days) with { Count = left }, newDate, RangeZone(recurrenceNode, own));
        }
        else
        {
            following["recurrence"]!["range"]!["startDate"] = Iso(newDate);
        }
        var created = GraphEventMapper.Rebuild(new Item(Guid.NewGuid().ToString("N"), collection.Id, ItemKind.Event, null, "{}"),
            following.ToJsonString(), zone);
        return (Truncate(master, at.AddDays(-1), zone), created);
    }

    /// <summary>The master ending before the occurrence at <paramref name="recurrence"/>; null on the first occurrence.</summary>
    public static Item? DeleteFollowing(Item master, DateTimeOffset recurrence, TimeZoneInfo zone)
    {
        var ev = GraphEvents.Parse(master.RawPayload);
        var own = GraphEvents.OwnZone(ev, zone);
        var occurrence = Occurrence(master, recurrence, zone);
        var at = OwnDate(occurrence.Start, zone, own, occurrence.AllDay);
        return ev["recurrence"] is JsonObject && at > StartDate(master, ev, zone) ? Truncate(master, at.AddDays(-1), zone) : null;
    }

    // ---- Rule changes (R3) ----

    /// <summary>
    /// A new repeat rule: for the whole event, or with <paramref name="following"/> from the occurrence at
    /// <paramref name="recurrence"/> on (a split). A weekly start moves to the first chosen weekday. Null when the
    /// rule is unchanged. <paramref name="series"/> are the stored occurrences and exceptions, to count what is lost.
    /// </summary>
    public static GraphSeriesChange? ChangeRule(Item master, DateTimeOffset? recurrence, bool following, RepeatRule rule, Collection collection,
        IReadOnlyList<Item> series, TimeZoneInfo zone)
    {
        var current = ReadRule(master, zone) ?? throw new EditRefusedException("This repeat pattern can't be changed here.");
        if (current.Equals(rule))
        {
            return null;
        }
        var ev = GraphEvents.Parse(master.RawPayload);
        var own = GraphEvents.OwnZone(ev, zone);
        var first = GraphEventMapper.Read(master, null, zone);
        if (current.Frequency == RepeatFrequency.None)
        {
            return new GraphSeriesChange(WithRule(master, first, rule, collection, zone, own), null, false, 0);
        }

        if (following && recurrence is { } r)
        {
            var occurrence = Occurrence(master, r, zone);
            var at = OwnDate(occurrence.Start, zone, own, occurrence.AllDay);
            if (at > StartDate(master, ev, zone))
            {
                var lost = Exceptions(series, occurrence.Start, zone);
                // An unchanged count keeps counting from the original start.
                if (rule.Count is { } count && count == current.Count)
                {
                    rule = rule with { Count = Math.Max(1, count - Dates(current, StartDate(master, ev, zone)).TakeWhile(d => d < at).Count()) };
                }
                var next = GraphEvents.MoveTo(master, collection, zone);
                var nextFields = first with { Start = occurrence.Start, End = occurrence.End };
                return new GraphSeriesChange(Truncate(master, at.AddDays(-1), zone),
                    WithRule(next, nextFields, rule, collection, zone, own), false, lost);
            }
        }

        var all = Exceptions(series, null, zone);
        if (rule.Frequency == RepeatFrequency.None)
        {
            var single = GraphEvents.MoveTo(master, collection, zone);
            var json = GraphEvents.Parse(single.RawPayload);
            json.Remove("recurrence");
            return new GraphSeriesChange(null, GraphEventMapper.Rebuild(single, json.ToJsonString(), zone), true, all);
        }
        return new GraphSeriesChange(WithRule(master, first, rule, collection, zone, own), null, false, all);
    }

    /// <summary>How many stored exceptions start at or after <paramref name="from"/> (all when null).</summary>
    public static int Exceptions(IReadOnlyList<Item> series, DateTime? from, TimeZoneInfo zone) =>
        series.Count(i => GraphClient.Text(System.Text.Json.JsonDocument.Parse(i.RawPayload).RootElement, "type") == "exception"
            && (from is null || i.StartUtc is not { } s || TimeZoneInfo.ConvertTime(s, zone).DateTime >= from.Value));

    /// <summary>
    /// The editor's fields for the occurrence that started at <paramref name="recurrence"/>: the master's text with
    /// that occurrence's times (the master's length).
    /// </summary>
    public static EventFields Occurrence(Item master, DateTimeOffset recurrence, TimeZoneInfo zone)
    {
        var fields = GraphEventMapper.Read(master, null, zone);
        var start = fields.AllDay ? TimeZoneInfo.ConvertTime(recurrence, zone).Date : TimeZoneInfo.ConvertTime(recurrence, zone).DateTime;
        return fields with { Start = start, End = start + (fields.End - fields.Start) };
    }

    // ---- Internals ----

    /// <summary>The event (a single event or a series' copy) with <paramref name="rule"/>, its start moved to the rule's first date.</summary>
    private static Item WithRule(Item item, EventFields fields, RepeatRule rule, Collection collection, TimeZoneInfo zone, TimeZoneInfo own)
    {
        var ev = GraphEvents.Parse(item.RawPayload);
        var changed = GraphEvents.Changed(ev);
        if (rule.Frequency == RepeatFrequency.None)
        {
            ev["recurrence"] = null;
            changed.Add("recurrence");
            return GraphEvents.WithChanged(item, ev, changed, zone);
        }
        var date = Date(fields.Start);
        var firstDate = Dates(rule, date).First();
        var moved = fields with { Start = fields.Start.AddDays(firstDate.DayNumber - date.DayNumber), End = fields.End.AddDays(firstDate.DayNumber - date.DayNumber) };
        GraphEvents.SetTimes(ev, moved, zone, own);
        ev["recurrence"] = Recurrence(rule, OwnDate(moved.Start, zone, own, moved.AllDay), own);
        changed.UnionWith(["start", "end", "isAllDay", "recurrence"]);
        return GraphEvents.WithChanged(item, ev, changed, zone);
    }

    /// <summary>The master with its range ending on <paramref name="last"/> (a numbered range becomes an end date).</summary>
    private static Item Truncate(Item master, DateOnly last, TimeZoneInfo zone)
    {
        var ev = GraphEvents.Parse(master.RawPayload);
        var range = (JsonObject)ev["recurrence"]!["range"]!;
        range["type"] = "endDate";
        range["endDate"] = Iso(last);
        range.Remove("numberOfOccurrences");
        var changed = GraphEvents.Changed(ev);
        changed.Add("recurrence");
        return GraphEvents.WithChanged(master, ev, changed, zone);
    }

    /// <summary>A weekly rule's days moved by <paramref name="days"/>; the other rules follow the start date.</summary>
    private static RepeatRule Shift(RepeatRule rule, int days) =>
        rule.Frequency == RepeatFrequency.Weekly && days % 7 != 0
            ? rule with { Weekdays = rule.Weekdays.Select(d => (DayOfWeek)((((int)d + days) % 7 + 7) % 7)).ToList() }
            : rule;

    /// <summary>The series' first date: its range's start date, else its start in its own timezone.</summary>
    private static DateOnly StartDate(Item master, JsonObject ev, TimeZoneInfo zone)
    {
        if (DateOnly.TryParse(Str(ev["recurrence"]?["range"]?["startDate"]), CultureInfo.InvariantCulture, out var date))
        {
            return date;
        }
        var fields = GraphEventMapper.Read(master, null, zone);
        return OwnDate(fields.Start, zone, GraphEvents.OwnZone(ev, zone), fields.AllDay);
    }

    /// <summary>A wall time in the editor's timezone as a date in the event's own timezone (all-day dates as they are).</summary>
    private static DateOnly OwnDate(DateTime wall, TimeZoneInfo zone, TimeZoneInfo own, bool allDay) =>
        allDay || zone.Id == own.Id ? Date(wall) : Date(TimeZoneInfo.ConvertTime(GraphEventMapper.Local(wall, zone), own).DateTime);

    private static TimeZoneInfo RangeZone(JsonObject recurrence, TimeZoneInfo own) =>
        Str(recurrence["range"]?["recurrenceTimeZone"]) is { } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var z) ? z : own;

    private static DateOnly Date(DateTime t) => DateOnly.FromDateTime(t);

    private static DateOnly NthWeekday(DateOnly month, DayOfWeek day, int week)
    {
        if (week > 0)
        {
            var first = month.AddDays(((int)day - (int)month.DayOfWeek + 7) % 7);
            return first.AddDays(7 * (week - 1));
        }
        var last = month.AddMonths(1).AddDays(-1);
        return last.AddDays(-(((int)last.DayOfWeek - (int)day + 7) % 7));
    }

    private static int? Week(string? index, DateOnly start) => index switch
    {
        "last" when RepeatRule.IsLastWeek(start) => -1,
        _ when Array.IndexOf(Ordinals, index) is >= 0 and var i && RepeatRule.WeekOfMonth(start) == i + 1 => i + 1,
        _ => null,
    };

    private static DayOfWeek? Day(string? name) => Enum.TryParse<DayOfWeek>(name, ignoreCase: true, out var d) ? d : null;

    private static JsonArray Days(IEnumerable<DayOfWeek> days) =>
        new(days.Distinct().Order().Select(d => (JsonNode)JsonValue.Create(d.ToString().ToLowerInvariant())!).ToArray());

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
