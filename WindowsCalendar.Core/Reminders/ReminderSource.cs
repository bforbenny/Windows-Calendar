using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Core.Reminders;

/// <summary>
/// Collects the reminders of visible calendars and task lists with "Reminders on" (reminders-notifications R1, R5, R7).
/// Events come from the occurrence index, one set per occurrence; cancelled ones are not in it. Tasks come from the
/// tasks table; completed tasks have none. Hidden calendars are not pulled by sync, so their reminders could be stale
/// and are left out.
/// </summary>
public static class ReminderSource
{
    /// <summary>Occurrences starting up to this far ahead are read, which covers alarms up to two weeks before.</summary>
    public static readonly TimeSpan LookAhead = TimeSpan.FromDays(15);

    public static IReadOnlyList<Reminder> Collect(LocalStore store, DateTimeOffset from, TimeZoneInfo zone)
    {
        var collections = new Dictionary<string, (Collection Collection, AccountType Type)>();
        foreach (var account in store.GetAccounts())
        {
            foreach (var c in store.GetCollections(account.Id))
            {
                collections[c.Id] = (c, account.Type);
            }
        }

        var reminders = new List<Reminder>();
        var items = new Dictionary<string, Item?>();
        foreach (var o in store.GetOccurrenceViews(from, from + LookAhead))
        {
            if (o.Kind != ItemKind.Event || !collections.TryGetValue(o.CollectionId, out var c) || !c.Collection.RemindersOn)
            {
                continue;
            }
            if (!items.TryGetValue(o.ItemId, out var item))
            {
                items[o.ItemId] = item = store.GetItem(o.ItemId);
            }
            if (item is null)
            {
                continue;
            }
            var master = item.SeriesId is { } seriesId ? store.FindItemByRemoteId(item.CollectionId, seriesId) : null;
            foreach (var fire in ReminderAlarms.ForEvent(item, master, c.Type, o.StartUtc, o.EndUtc, o.RecurrenceUtc, zone))
            {
                reminders.Add(new Reminder(o.ItemId, o.CollectionId, ItemKind.Event, o.RecurrenceUtc, fire, Title(o.Summary),
                    o.StartUtc, o.EndUtc, o.AllDay, o.Location, c.Collection.Name, o.JoinUrl, ShowLateUntil: o.StartUtc));
            }
        }

        foreach (var task in store.GetTasks())
        {
            if (TaskQuery.IsCompleted(task) || !collections.TryGetValue(task.CollectionId, out var c) || !c.Collection.RemindersOn)
            {
                continue;
            }
            var (due, allDay, lateUntil) = Due(task, c.Type, zone);
            foreach (var fire in ReminderAlarms.ForTask(task, c.Type, zone))
            {
                reminders.Add(new Reminder(task.Id, task.CollectionId, ItemKind.Task, null, fire, Title(task.Summary),
                    due, null, allDay, task.Location, c.Collection.Name, null, lateUntil));
            }
        }
        return reminders;
    }

    private static string Title(string? summary) => string.IsNullOrWhiteSpace(summary) ? "(No title)" : summary;

    /// <summary>A task's due time, whether it is a date, and until when a late reminder still shows (end of the due day for a date).</summary>
    private static (DateTimeOffset? Due, bool AllDay, DateTimeOffset? LateUntil) Due(Item task, AccountType type, TimeZoneInfo zone)
    {
        if (type == AccountType.MS365)
        {
            return GraphTaskMapper.DueDate(task.RawPayload) is { } day
                ? (GraphEventMapper.Local(day.ToDateTime(TimeOnly.MinValue), zone), true, GraphEventMapper.Local(day.AddDays(1).ToDateTime(TimeOnly.MinValue), zone))
                : (null, false, null);
        }
        if (task.DueUtc is not { } due)
        {
            return (null, false, null);
        }
        if (!task.AllDay)
        {
            return (due, false, due);
        }
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(due, zone).DateTime);
        return (due, true, GraphEventMapper.Local(date.AddDays(1).ToDateTime(TimeOnly.MinValue), zone));
    }
}
