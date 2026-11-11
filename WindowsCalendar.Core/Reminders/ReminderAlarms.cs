using System.Text.Json;
using Ical.Net;
using Ical.Net.CalendarComponents;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Reminders;

/// <summary>
/// Reads when an item's reminders fire (reminders-notifications R1): Graph isReminderOn + reminderMinutesBeforeStart on
/// events and reminderDateTime on tasks; CalDAV DISPLAY alarms with a relative (to start or end) or absolute trigger.
/// </summary>
internal static class ReminderAlarms
{
    /// <summary>
    /// Fire times for one occurrence of an event. An MS365 occurrence without its own reminder fields takes its
    /// series'. A CalDAV occurrence uses its override's alarms when it has one, else the master's; an absolute trigger
    /// on a repeating master is skipped, as it would fire once for every occurrence.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> ForEvent(Item item, Item? master, AccountType type,
        DateTimeOffset start, DateTimeOffset end, DateTimeOffset? recurrence, TimeZoneInfo zone) =>
        type == AccountType.MS365 ? GraphEvent(item, master, start) : CalDavEvent(item, start, end, recurrence, zone);

    /// <summary>Fire times of a task's reminders.</summary>
    public static IReadOnlyList<DateTimeOffset> ForTask(Item task, AccountType type, TimeZoneInfo zone)
    {
        if (type == AccountType.MS365)
        {
            using var doc = Json(task.RawPayload);
            return doc is not null && IsTrue(doc.RootElement, "isReminderOn")
                && GraphTaskMapper.Time(doc.RootElement, "reminderDateTime", zone) is { } at
                ? [at]
                : [];
        }
        if (Load(task.RawPayload)?.Todos.FirstOrDefault() is not { } todo)
        {
            return [];
        }
        var startAt = todo.DtStart is { } s ? OccurrenceExpander.ToUtc(s, zone) : (DateTimeOffset?)null;
        var dueAt = todo.Due is { } d ? OccurrenceExpander.ToUtc(d, zone) : (DateTimeOffset?)null;
        return Fire(todo.Alarms, startAt ?? dueAt, dueAt ?? startAt, absolute: true, zone);
    }

    private static IReadOnlyList<DateTimeOffset> GraphEvent(Item item, Item? master, DateTimeOffset start)
    {
        foreach (var payload in new[] { item.RawPayload, master?.RawPayload })
        {
            using var doc = payload is null ? null : Json(payload);
            if (doc is null || !doc.RootElement.TryGetProperty("isReminderOn", out _))
            {
                continue;
            }
            var ev = doc.RootElement;
            return IsTrue(ev, "isReminderOn") && ev.TryGetProperty("reminderMinutesBeforeStart", out var m) && m.TryGetInt32(out var minutes)
                ? [start.AddMinutes(-minutes)]
                : [];
        }
        return [];
    }

    private static IReadOnlyList<DateTimeOffset> CalDavEvent(Item item, DateTimeOffset start, DateTimeOffset end,
        DateTimeOffset? recurrence, TimeZoneInfo zone)
    {
        if (Load(item.RawPayload) is not { } calendar)
        {
            return [];
        }
        var overrideEvent = recurrence is { } r
            ? calendar.Events.FirstOrDefault(e => e.RecurrenceIdentifier is { } id && OccurrenceExpander.ToUtc(id.StartTime, zone) == r)
            : null;
        var ev = overrideEvent ?? calendar.Events.FirstOrDefault(e => e.RecurrenceIdentifier is null);
        if (ev is null)
        {
            return [];
        }
        var absolute = overrideEvent is not null || !item.IsRecurring;
        return Fire(ev.Alarms, start, end, absolute, zone);
    }

    private static List<DateTimeOffset> Fire(IEnumerable<Alarm> alarms, DateTimeOffset? start, DateTimeOffset? end, bool absolute, TimeZoneInfo zone)
    {
        var times = new List<DateTimeOffset>();
        foreach (var alarm in alarms)
        {
            if (!string.Equals(alarm.Action, "DISPLAY", StringComparison.OrdinalIgnoreCase) || alarm.Trigger is not { } trigger)
            {
                continue;
            }
            if (trigger.IsRelative && trigger.Duration is { } offset)
            {
                var anchor = string.Equals(trigger.Related, "END", StringComparison.OrdinalIgnoreCase) ? end : start;
                if (anchor is { } at)
                {
                    times.Add(at + offset.ToTimeSpanUnspecified());
                }
            }
            else if (!trigger.IsRelative && absolute && trigger.DateTime is { } dateTime)
            {
                times.Add(OccurrenceExpander.ToUtc(dateTime, zone));
            }
        }
        return times.Distinct().ToList();
    }

    private static bool IsTrue(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static JsonDocument? Json(string payload)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Calendar? Load(string payload)
    {
        try
        {
            return Calendar.Load(payload);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
