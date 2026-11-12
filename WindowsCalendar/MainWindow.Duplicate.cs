using System;
using Serilog;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// Duplicate an event (duplicate-event, first slice): the card's Duplicate menu and Ctrl+D copy it at the same time in
/// the same calendar and open the copy in the editor; Ctrl+drag copies it to the drop. Undo deletes the copy.
/// </summary>
public sealed partial class MainWindow
{
    private void Duplicate(DisplayEvent e, DragTarget? at)
    {
        if (Store is not { } store || !e.CanDuplicate || store.GetItem(e.ItemId) is not { } item)
        {
            return;
        }
        var zone = TimeZoneInfo.Local;
        (bool, DateTime, DateTime)? times = at is null ? null : (at.AllDay, at.Start, at.End);
        Item copy;
        try
        {
            if (IsGraph(e.Collection))
            {
                var master = item.SeriesId is { } series ? store.FindItemByRemoteId(item.CollectionId, series) : null;
                if (item.SeriesId is not null && master is null)
                {
                    ShowUndo("Can't duplicate this event", canUndo: false, "This repeating event is still syncing; try again shortly.");
                    return;
                }
                copy = GraphEvents.Duplicate(item, master, e.Collection, zone, times);
            }
            else
            {
                copy = CalDavEvents.Duplicate(item, e.Recurrence, e.Collection, zone, DateTimeOffset.UtcNow, times);
            }
            store.CreateItem(copy);
        }
        catch (EditRefusedException ex)
        {
            ShowUndo("Can't duplicate this event", canUndo: false, ex.Message);
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Duplicating event {ItemId} failed", e.ItemId);
            return;
        }
        Log.Information("Event {ItemId} duplicated as {CopyId}", item.Id, copy.Id);

        // Undo deletes the copy (duplicate-event R2, undo R4).
        _lastChange = (copy.Id, null, copy.RawPayload, null);
        ShowUndo("Event duplicated", canUndo: true, note: copy.StartUtc is { } start && copy.EndUtc is { } end
            ? ScheduleWarning.For(store, copy.Id, start, end, copy.AllDay, e.ShowAs) : null);
        _selectedKey = EventCard.SelectedKey = $"{copy.Id}|";
        _selectedDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(copy.StartUtc ?? DateTimeOffset.Now, zone).DateTime);
        RenderView();
        if (at is null)
        {
            ShowEventEditor();
            _editor.OpenExisting(copy.Id, null, _collections);
        }
    }

    /// <summary>
    /// Deletes the copy: an unsent create is simply dropped from the outbox, a sent one is deleted on the server
    /// (undo R4). Edits made to the copy since are deleted with it.
    /// </summary>
    private void UndoDuplicate(LocalStore store, string copyId)
    {
        try
        {
            if (_editor.ItemId == copyId)
            {
                _editor.Hide();
            }
            if (_taskEditor.ItemId == copyId)
            {
                _taskEditor.Hide();
            }
            if (store.GetItem(copyId) is not null)
            {
                store.DeleteItem(copyId);
            }
            BannerFade.SetOpen(UndoBar, false);
            Log.Information("Added item {ItemId} undone", copyId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Undoing duplicate {ItemId} failed", copyId);
        }
        RenderView();
    }
}
