using System;
using System.Threading.Tasks;
using Serilog;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>Drag and undo of MS365 events (sync-ms365 second slice, recurring-events MS365 interim).</summary>
public sealed partial class MainWindow
{
    private bool IsGraph(Collection collection) => _graphAccounts.Contains(collection.AccountId);

    /// <summary>
    /// Undo refusal for an MS365 series change that made Exchange drop changed occurrences: they can't be put back
    /// (undo R5).
    /// </summary>
    internal const string LostExceptionsRefusal = "Changed occurrences that were dropped can't be restored, so this change can't be undone.";

    /// <summary>
    /// A drop on an MS365 event: a single event or one occurrence is PATCHed; for a series the scope is asked, and a
    /// change that moves the series' time asks first when stored exceptions would be lost.
    /// </summary>
    private async Task DropGraphAsync(DisplayEvent e, DragTarget target)
    {
        if (Store is not { } store || store.GetItem(e.ItemId) is not { } item)
        {
            return;
        }
        var zone = TimeZoneInfo.Local;
        var master = item.SeriesId is { } series ? store.FindItemByRemoteId(item.CollectionId, series) : null;
        var recurrence = item.StartUtc ?? e.Recurrence ?? default;
        var scope = (EditScope?)null;
        if (master is not null)
        {
            scope = await ScopeDialog.AskAsync("Move a repeating event", allowFollowing: true);
            if (scope is null)
            {
                return;
            }
        }
        var fields = GraphEventMapper.Read(item, master, zone) with { AllDay = target.AllDay, Start = target.Start, End = target.End };
        var changedId = item.Id;
        var lost = 0;
        Item? edited = null;
        Item? following = null;
        try
        {
            if (master is null || scope == EditScope.Occurrence)
            {
                edited = GraphEvents.Edit(item, master, fields, zone);
            }
            else
            {
                changedId = master.Id;
                var split = scope == EditScope.Following ? GraphRecurrence.SplitSeries(master, recurrence, fields, e.Collection, zone) : null;
                lost = GraphRecurrence.Exceptions(store.GetSeriesItems(item.CollectionId, master.RemoteId!),
                    split is null ? null : TimeZoneInfo.ConvertTime(recurrence, zone).DateTime, zone);
                if (!await LostDialog.ConfirmAsync("Move the series?", "Move", lost))
                {
                    return;
                }
                (edited, following) = split is { } s ? (s.Original, s.Following) : (GraphRecurrence.EditSeries(master, recurrence, fields, zone), null);
            }
            if (edited is null)
            {
                return;
            }
            using (var undo = store.BeginUndo("Event moved"))
            {
                if (following is not null)
                {
                    store.SplitItem(edited, following);
                }
                else
                {
                    store.UpdateItem(edited);
                }
                if (lost > 0)
                {
                    undo.Refuse(LostExceptionsRefusal);
                }
            }
            ShowUndo(scope switch
            {
                EditScope.Series => "Series moved",
                EditScope.Following => "Following events moved",
                _ => "Event moved",
            }, canUndo: true, note: ScheduleWarning.For(store, following?.Id ?? item.Id, target.Start, target.End,
                zone, zone, target.AllDay, e.ShowAs));
            Log.Information("Event {ItemId} moved by drag ({Scope})", changedId, scope?.ToString() ?? "single");
        }
        catch (EditRefusedException ex)
        {
            ShowUndo("Can't move this event", canUndo: false, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Moving event {ItemId} failed", e.ItemId);
        }
        RenderView();
        // The occurrence now starts at the drop.
        var wall = DateTime.SpecifyKind(target.Start, DateTimeKind.Unspecified);
        var to = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(zone.IsInvalidTime(wall) ? wall.AddHours(1) : wall, zone), TimeSpan.Zero);
        _editor.Reload(changedId, recurrence, to);
    }
}
