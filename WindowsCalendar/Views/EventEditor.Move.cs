using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Sync;

namespace WindowsCalendar.Views;

/// <summary>
/// The Calendar field (calendar-views R8): it lists the writable calendars of every account. A calendar in the same
/// account moves the event at once. A calendar in another account first shows a confirmation with what the event can't
/// take along, then queues a create there and a delete here as one linked group. Meetings and invitations stay in their
/// calendar.
/// </summary>
internal sealed partial class EventEditor
{
    private IReadOnlyDictionary<string, Collection> _all = new Dictionary<string, Collection>();

    /// <summary>The event's own calendar, and when it may move, every other writable calendar.</summary>
    private List<Collection> CalendarChoices(Collection current, bool movable)
    {
        var accounts = Store?.GetAccounts().Select(a => a.Id).ToHashSet() ?? [];
        return _all.Values
            .Where(c => c.Kind == CollectionKind.Calendar
                && (c.Id == current.Id || movable && !c.ReadOnly && c.SourceUrl is null && accounts.Contains(c.AccountId)))
            .ToList();
    }

    private void CalendarChanged()
    {
        if (!_loading && _itemId is null)
        {
            // A new event may have moved between MS365 and CalDAV (event-show-as R2).
            ShowShowAs(ReadShowAs());
        }
        if (!_loading && !_readOnly && _itemId is { } id && (_calendar.SelectedItem as ComboBoxItem)?.Tag is Collection target
            && Store?.GetItem(id) is { } item && _all.TryGetValue(item.CollectionId, out var from) && from.AccountId != target.AccountId)
        {
            _ = MoveToAccountAsync(from, target);
            return;
        }
        Save();
    }

    private void SelectCalendar(string collectionId)
    {
        var loading = _loading;
        _loading = true;
        _calendar.SelectedItem = _calendar.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as Collection)?.Id == collectionId);
        _loading = loading;
    }

    private async Task MoveToAccountAsync(Collection from, Collection target)
    {
        // Typed changes are saved to the event where it is now; the drop-down shows that calendar until the move is done.
        SelectCalendar(from.Id);
        Save();
        if (_asking || Store is not { } store || _itemId is not { } id || store.GetItem(id) is not { } item)
        {
            return;
        }
        var toGraph = IsGraphCalendar(target);
        TransferResult result;
        try
        {
            result = EventTransfer.Move(item, _graph, target, toGraph, TimeZoneInfo.Local, DateTimeOffset.UtcNow);
        }
        catch (EditRefusedException ex)
        {
            ShowNotice(ex.Message, InfoBarSeverity.Warning);
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Preparing the move of event {ItemId} failed", id);
            return;
        }

        var account = store.GetAccounts().FirstOrDefault(a => a.Id == target.AccountId)?.DisplayName ?? "the other account";
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = $"The event is copied to {target.Name} in {account}, then deleted from {from.Name}.",
            TextWrapping = TextWrapping.Wrap,
        });
        if (result.Lost.Count > 0)
        {
            content.Children.Add(new TextBlock { Text = "It can't take along:", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = string.Join("\n", result.Lost.Select(l => "• " + l)), TextWrapping = TextWrapping.Wrap });
        }
        _asking = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Move to {target.Name}?",
                Content = content,
                PrimaryButtonText = "Move",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }
        finally
        {
            _asking = false;
        }

        string movedId;
        try
        {
            movedId = store.MoveItem(item.Id, result.Moved);
            Log.Information("Event {ItemId} moved to another account as {MovedId}", item.Id, movedId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Moving event {ItemId} to another account failed", item.Id);
            return;
        }
        Changed?.Invoke();
        // A CalDAV series keeps its occurrence times; an MS365 copy opens at its first occurrence.
        var recurrence = toGraph ? null : _recurrence;
        Hide();
        await OpenExistingAsync(movedId, recurrence, _all);
    }
}
