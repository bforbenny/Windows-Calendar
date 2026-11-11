using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;
using Serilog;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Views;

/// <summary>
/// MS365 events in the editor (sync-ms365 second slice, recurring-events MS365 interim). For a repeating event
/// <c>_itemId</c> is the series master and <c>_recurrence</c> the open occurrence's start, as for CalDAV;
/// <see cref="_occurrenceId"/> is the occurrence's own event, which This event changes. Meetings are view-only.
/// </summary>
internal sealed partial class EventEditor
{
    private string? _occurrenceId;

    private bool IsGraphCalendar(Collection collection) =>
        Store?.GetAccounts().FirstOrDefault(a => a.Id == collection.AccountId)?.Type == AccountType.MS365;

    private void OpenGraph(Item item, Collection collection, IReadOnlyDictionary<string, Collection> collections)
    {
        var zone = TimeZoneInfo.Local;
        var master = item.SeriesId is { } series ? Store!.FindItemByRemoteId(item.CollectionId, series) : null;
        var waiting = item.SeriesId is not null && master is null;
        _graph = true;
        _itemId = master?.Id ?? item.Id;
        _occurrenceId = master is null ? null : item.Id;
        _recurrence = master is null ? null : item.StartUtc;
        _scope = null;
        _allowFollowing = master is not null;
        _readOnly = collection.ReadOnly || waiting || !GraphEvents.CanEdit(item, master);
        // Your own reminder on a meeting stays editable (reminders-notifications R5a).
        _reminderOnly = _readOnly && !collection.ReadOnly && !waiting;
        _savedRule = GraphRecurrence.ReadRule(master ?? item, zone);
        _ruleAutosave = master is null;
        // A series moves to another calendar in Outlook (its exceptions would be lost here). Meetings and invitations
        // are read-only, so they stay in their calendar (calendar-views R8).
        var movable = !_readOnly && master is null && !item.IsRecurring;
        Show("Event", GraphEventMapper.Read(item, master, zone), CalendarChoices(collection, movable), collection.Id);
        _calendar.IsEnabled = movable;
        var message = collection.ReadOnly ? "This calendar is read-only."
            : waiting ? "This repeating event is still syncing; try again shortly."
            : _readOnly ? "Meetings can't be changed from this app yet, except your reminder."
            : _savedRule is null ? "Repeats. This repeat pattern can't be changed here." : "";
        ShowNotice(message, InfoBarSeverity.Informational);
    }

    /// <summary>The open event's fields: the occurrence's own until the whole series is chosen, then the series' at that occurrence.</summary>
    private EventFields GraphFields(Item item)
    {
        var zone = TimeZoneInfo.Local;
        if (_recurrence is not { } recurrence)
        {
            return GraphEventMapper.Read(item, null, zone);
        }
        var occurrence = _occurrenceId is { } id ? Store?.GetItem(id) : null;
        return _scope is EditScope.Series or EditScope.Following || occurrence is null
            ? GraphRecurrence.Occurrence(item, recurrence, zone)
            : GraphEventMapper.Read(occurrence, item, zone);
    }

    /// <summary>A new MS365 event, with the repeat chosen before the title. Returns its first occurrence when it repeats.</summary>
    private DateTimeOffset? CreateGraph(LocalStore store, Collection target, EventFields fields)
    {
        var zone = TimeZoneInfo.Local;
        var created = GraphEvents.Create(target, fields, zone);
        var rule = _repeat.Read();
        if (GraphRecurrence.ChangeRule(created, null, false, rule, target, [], zone)?.Updated is { } repeating)
        {
            created = repeating;
        }
        store.CreateItem(created);
        _graph = true;
        _itemId = created.Id;
        _occurrenceId = null;
        Log.Information("Event {ItemId} created", created.Id);
        return rule.Frequency == RepeatFrequency.None ? null : created.StartUtc;
    }

    /// <summary>A change to an MS365 event: a single event at once (a calendar change moves it); a series asks the scope.</summary>
    private void SaveGraph(LocalStore store, Item item, EventFields fields, Collection target)
    {
        var zone = TimeZoneInfo.Local;
        if (_recurrence is { } recurrence)
        {
            SaveGraphRecurring(item, recurrence, fields, target);
            return;
        }
        if (target.Id != item.CollectionId)
        {
            _itemId = store.MoveItem(item.Id, GraphEvents.MoveTo(item, target, zone));
            item = store.GetItem(_itemId)!;
            Log.Information("Event {ItemId} moved to another calendar", _itemId);
        }
        if (GraphEvents.Edit(item, null, fields, zone) is { } edited)
        {
            store.UpdateItem(edited);
        }
        Changed?.Invoke();
        RefreshRule();
    }

    /// <summary>
    /// As <see cref="SaveRecurring"/>: This event PATCHes the occurrence; All events the master; This and following
    /// splits. A change that moves the series' time asks first when exceptions would be lost.
    /// </summary>
    private async void SaveGraphRecurring(Item master, DateTimeOffset recurrence, EventFields fields, Collection collection)
    {
        var zone = TimeZoneInfo.Local;
        try
        {
            var current = GraphFields(master);
            if (!CalDavRecurrence.Changed(current, fields))
            {
                return;
            }
            if (_scope is null)
            {
                if (_asking)
                {
                    return;
                }
                _asking = true;
                var scope = await ScopeDialog.AskAsync("Change a repeating event", _allowFollowing);
                _asking = false;
                if (_itemId != master.Id || _recurrence != recurrence)
                {
                    return;
                }
                if (scope is null)
                {
                    ShowFields(current);
                    return;
                }
                _scope = scope;
                fields = ReadFields();
                master = Store?.GetItem(master.Id) ?? master;
            }
            var store = Store!;
            var moved = fields.AllDay != current.AllDay || fields.Start != current.Start || fields.End != current.End;
            var series = store.GetSeriesItems(collection.Id, master.RemoteId ?? "");

            if (_scope == EditScope.Occurrence)
            {
                if (_occurrenceId is not { } id || store.GetItem(id) is not { } occurrence)
                {
                    Hide();
                    return;
                }
                if (GraphEvents.Edit(occurrence, master, fields, zone) is { } edited)
                {
                    store.UpdateItem(edited);
                    _recurrence = edited.StartUtc;
                }
            }
            else if (_scope == EditScope.Following
                && GraphRecurrence.SplitSeries(master, recurrence, fields, collection, zone) is { } split)
            {
                var lost = GraphRecurrence.Exceptions(series, TimeZoneInfo.ConvertTime(recurrence, zone).DateTime, zone);
                if (!await LostDialog.ConfirmAsync("Change the following events?", "Change", lost) || _itemId != master.Id)
                {
                    ShowFields(current);
                    return;
                }
                store.SplitItem(split.Original, split.Following);
                _itemId = split.Following.Id;
                _occurrenceId = null;
                _recurrence = split.Following.StartUtc;
                _scope = EditScope.Series;
                Log.Information("Repeating event {ItemId} split", master.Id);
            }
            else
            {
                if (moved && !await LostDialog.ConfirmAsync("Change the series?", "Change", GraphRecurrence.Exceptions(series, null, zone)))
                {
                    ShowFields(current);
                    return;
                }
                if (GraphRecurrence.EditSeries(master, recurrence, fields, zone) is { } edited)
                {
                    store.UpdateItem(edited);
                    _recurrence = recurrence + (Utc(fields.Start) - Utc(current.Start));
                }
                // From the first occurrence, "this and following" is the whole series.
                _scope = EditScope.Series;
            }
            _shownStart = fields.Start;
            Log.Information("Repeating event {ItemId} changed ({Scope})", master.Id, _scope);
            Changed?.Invoke();
            RefreshRule();
        }
        catch (EditRefusedException ex)
        {
            ShowNotice(ex.Message, InfoBarSeverity.Warning);
            if (_itemId is { } id && Store?.GetItem(id) is { } latest)
            {
                ShowFields(GraphFields(latest));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving repeating event {ItemId} failed", master.Id);
        }
    }

    /// <summary>A single MS365 event's repeat saves at once (it becomes a series, drawn once the sync brings its occurrences).</summary>
    private void SaveGraphRule(LocalStore store, Item item, Collection target)
    {
        var change = GraphRecurrence.ChangeRule(item, null, false, _repeat.Read(), target, [], TimeZoneInfo.Local);
        if (change?.Updated is not { } updated)
        {
            return;
        }
        store.UpdateItem(updated);
        GraphRuleSaved(_repeat.Read().Frequency == RepeatFrequency.None ? null : updated.StartUtc);
    }

    /// <summary>Apply on an MS365 series: This and following / All, the lost-exceptions question, then one save.</summary>
    private async Task ApplyGraphRuleAsync(LocalStore store, Item master, DateTimeOffset recurrence, Collection target)
    {
        var rule = _repeat.Read();
        var scope = await ScopeDialog.AskAsync("Change the repeat pattern", true, allowOccurrence: false);
        if (scope is null || _itemId != master.Id)
        {
            return;
        }
        master = store.GetItem(master.Id) ?? master;
        var change = GraphRecurrence.ChangeRule(master, recurrence, scope == EditScope.Following, rule, target,
            store.GetSeriesItems(target.Id, master.RemoteId ?? ""), TimeZoneInfo.Local);
        if (change is null)
        {
            _repeat.ApplyVisible = false;
            return;
        }
        if (!await LostDialog.ConfirmAsync("Change the repeat pattern?", "Change", change.Lost) || _itemId != master.Id)
        {
            return;
        }
        var repeats = rule.Frequency != RepeatFrequency.None;
        if (change is { Updated: { } original, Created: { } following })
        {
            store.SplitItem(original, following);
            _itemId = following.Id;
            GraphRuleSaved(repeats ? following.StartUtc : null);
        }
        else if (change is { ReplacesMaster: true, Created: { } single })
        {
            _itemId = store.MoveItem(master.Id, single);
            GraphRuleSaved(null);
        }
        else if (change.Updated is { } updated)
        {
            store.UpdateItem(updated);
            GraphRuleSaved(updated.StartUtc);
        }
    }

    /// <summary>After a rule change: the panel follows the series' first occurrence, or the single event.</summary>
    private void GraphRuleSaved(DateTimeOffset? recurrence)
    {
        _occurrenceId = null;
        _recurrence = recurrence;
        _scope = recurrence is null ? null : EditScope.Series;
        _ruleAutosave = recurrence is null;
        _allowFollowing = recurrence is not null;
        _calendar.IsEnabled = recurrence is null;
        _savedRule = null;
        Log.Information("Repeat of event {ItemId} changed", _itemId);
        Changed?.Invoke();
        Reload(_itemId!);
    }

    /// <summary>
    /// Delete of an MS365 repeating event: This event deletes the occurrence; This and following ends the series
    /// before it (with Undo). Returns false for the entire series, which the caller confirms and deletes.
    /// </summary>
    private bool DeleteGraphOccurrences(LocalStore store, string masterId, DateTimeOffset recurrence, EditScope scope)
    {
        var zone = TimeZoneInfo.Local;
        if (store.GetItem(masterId) is not { } master)
        {
            return true;
        }
        if (scope == EditScope.Following && GraphRecurrence.DeleteFollowing(master, recurrence, zone) is { } truncated)
        {
            store.UpdateItem(truncated);
            Log.Information("Occurrence(s) of event {ItemId} deleted ({Scope})", masterId, scope);
            OccurrenceDeleted?.Invoke("Following events deleted", masterId, master.RawPayload, truncated.RawPayload);
            return true;
        }
        if (scope == EditScope.Occurrence && _occurrenceId is { } id)
        {
            store.DeleteItem(id);
            Log.Information("Occurrence(s) of event {ItemId} deleted ({Scope})", masterId, scope);
            return true;
        }
        return false;
    }

    private static DateTimeOffset Utc(DateTime wall)
    {
        var zone = TimeZoneInfo.Local;
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(zone.IsInvalidTime(wall) ? wall.AddHours(1) : wall, zone), TimeSpan.Zero);
    }
}
