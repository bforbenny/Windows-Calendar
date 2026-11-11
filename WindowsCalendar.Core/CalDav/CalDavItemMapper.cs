using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.CalDav;

/// <summary>
/// Turns a CalDAV resource into a cached item (local-store-offline R2): the iCalendar text is kept as-is and the
/// normalised columns come from the master component (the one without RECURRENCE-ID).
/// </summary>
internal static class CalDavItemMapper
{
    /// <summary>
    /// The item for a resource, with empty Id and CollectionId for the caller to fill in;
    /// null if it holds neither VEVENT nor VTODO or cannot be read.
    /// </summary>
    public static Item? Map(string href, string etag, string data, TimeZoneInfo zone)
    {
        Calendar? calendar;
        try
        {
            calendar = CustomTimeZones.Load(data);
        }
        catch (Exception)
        {
            return null;
        }
        if (calendar is null)
        {
            return null;
        }

        var ev = calendar.Events.FirstOrDefault(e => e.RecurrenceIdentifier is null) ?? calendar.Events.FirstOrDefault();
        if (ev is not null)
        {
            var start = Utc(ev.DtStart, zone);
            return new Item("", "", ItemKind.Event, href, data,
                Summary: ev.Summary, StartUtc: start, EndUtc: Utc(ev.DtEnd ?? ev.DtStart?.Add(ev.EffectiveDuration), zone) ?? start,
                AllDay: ev.DtStart is { HasTime: false }, TimeZone: ev.DtStart?.TzId, Location: ev.Location,
                Status: ShowAsValues.ToGraph(CalDavEvents.ReadShowAs(ev)), IsRecurring: IsRecurring(ev), Etag: etag, LastModifiedUtc: Utc(ev.LastModified, zone),
                JoinUrl: JoinLink.FromCalDav(ev)?.Url);
        }

        var todo = calendar.Todos.FirstOrDefault(t => t.RecurrenceIdentifier is null) ?? calendar.Todos.FirstOrDefault();
        if (todo is not null)
        {
            return new Item("", "", ItemKind.Task, href, data,
                Summary: todo.Summary, StartUtc: Utc(todo.DtStart, zone), AllDay: todo.Due is { HasTime: false },
                TimeZone: todo.Due?.TzId ?? todo.DtStart?.TzId, Location: todo.Location, Status: todo.Status,
                DueUtc: Utc(todo.Due, zone), CompletedUtc: Utc(todo.Completed, zone),
                IsRecurring: IsRecurring(todo), Etag: etag, LastModifiedUtc: Utc(todo.LastModified, zone));
        }

        return null;
    }

    private static bool IsRecurring(RecurringComponent c) => c.RecurrenceRule is not null || c.Properties.ContainsKey("RDATE");

    private static DateTimeOffset? Utc(CalDateTime? t, TimeZoneInfo zone) => t is null ? null : OccurrenceExpander.ToUtc(t, zone);
}
