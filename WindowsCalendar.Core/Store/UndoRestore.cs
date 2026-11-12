using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;

namespace WindowsCalendar.Core.Store;

/// <summary>The earlier version an undo writes back (undo R4, R8), for <see cref="LocalStore.UndoLast"/>.</summary>
public static class UndoRestore
{
    /// <summary>
    /// With a <paramref name="current"/> item: the earlier payload put back through its mapper (an edit). Without one
    /// (deleted on the server): CalDAV re-creates it with the same UID and href; MS365 re-creates it with a new id
    /// from the fields a create may write (no attendees or online meeting, R8), and can't re-create a single
    /// occurrence of a series (null). An item the server never had comes back as it was.
    /// </summary>
    public static Item? For(Item before, Item? current, Collection collection, bool graph, TimeZoneInfo zone)
    {
        if (current is not null)
        {
            return (before.Kind, graph) switch
            {
                (ItemKind.Task, true) => GraphTasks.Restore(current, before.RawPayload, zone),
                (ItemKind.Task, false) => CalDavTasks.Restore(current, before.RawPayload, zone),
                (_, true) => GraphEvents.Restore(current, before.RawPayload, zone),
                _ => CalDavEvents.Restore(current, before.RawPayload, zone),
            };
        }
        if (!graph)
        {
            return before with { Etag = null };
        }
        if (before.RemoteId is null)
        {
            return before;
        }
        if (before.SeriesId is not null)
        {
            return null;
        }
        return before.Kind == ItemKind.Task ? GraphTasks.MoveTo(before, collection, zone) : GraphEvents.MoveTo(before, collection, zone);
    }
}
