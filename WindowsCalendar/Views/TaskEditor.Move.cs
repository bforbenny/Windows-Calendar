using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Serilog;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Sync;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;
using ContentDialogButton = Wpf.Ui.Controls.ContentDialogButton;
using ContentDialogResult = Wpf.Ui.Controls.ContentDialogResult;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace WindowsCalendar.Views;

/// <summary>
/// The List field (tasks R11): every writable task list, across accounts. A new task is only re-targeted. An existing
/// task moves when a list is chosen in the open drop-down (Enter, click), not on arrow keys while it is closed; when
/// something can't be taken along (always, from To Do) it first shows a confirmation listing it. The move queues a create there and a delete here as one linked group, and the editor then shows the moved
/// task. Moves are not undoable.
/// </summary>
internal sealed partial class TaskEditor
{
    private IReadOnlyDictionary<string, Collection> _all = new Dictionary<string, Collection>();
    private bool _asking;
    // Esc in the open List drop-down abandons the choice.
    private bool _listCancelled;

    /// <summary>The writable task lists, by account then sidebar order; set by the main window.</summary>
    public Func<List<Collection>>? ListChoices { get; set; }

    /// <summary>Fills the drop-down with the writable lists and the task's own list, selected, without moving anything.</summary>
    private void ShowLists(Collection current)
    {
        var choices = ListChoices?.Invoke() ?? [];
        if (choices.All(c => c.Id != current.Id))
        {
            choices.Insert(0, current);
        }
        var accounts = Store?.GetAccounts() ?? [];
        var loading = _loading;
        _loading = true;
        _listBox.Items.Clear();
        foreach (var c in choices)
        {
            var label = new SpacedStack { Orientation = Orientation.Horizontal, Spacing = 8 };
            label.Children.Add(ShapeMarker.Create(c.Shape, Theme.Calendar(c.Colour).Swatch));
            label.Children.Add(new TextBlock { Text = c.Name });
            if (accounts.FirstOrDefault(a => a.Id == c.AccountId) is { } account)
            {
                label.Children.Add(new TextBlock { Text = CollectionSource.For(account), Foreground = Theme.Brush("TextSecondaryBrush") });
            }
            _listBox.Items.Add(new ComboBoxItem { Content = label, Tag = c });
        }
        _listBox.SelectedIndex = choices.FindIndex(c => c.Id == current.Id);
        _loading = loading;
    }

    private void SelectList(string collectionId)
    {
        var loading = _loading;
        _loading = true;
        _listBox.SelectedItem = _listBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as Collection)?.Id == collectionId);
        _loading = loading;
    }

    private void ListSelectionChanged()
    {
        if (_loading)
        {
            return;
        }
        if (_newList is not null)
        {
            ListChanged();
        }
        else if (!_listBox.IsDropDownOpen && _collection is { } current)
        {
            // Arrow keys, type-ahead or the wheel on the closed drop-down don't choose a list.
            SelectList(current.Id);
        }
    }

    private void ListDropDownClosed()
    {
        if (_listCancelled)
        {
            _listCancelled = false;
            if (_collection is { } current)
            {
                SelectList(current.Id);
            }
            return;
        }
        ListChanged();
    }

    private void ListChanged()
    {
        if (_loading || (_listBox.SelectedItem as ComboBoxItem)?.Tag is not Collection target)
        {
            return;
        }
        if (_newList is not null)
        {
            // A task not created yet goes to the new list; due time is a Radicale field, and Radicale repeat stays view-only.
            _newList = _collection = target;
            _calDav = IsCalDav(target);
            _timeField.Visibility = _calDav ? Visibility.Visible : Visibility.Collapsed;
            if (!_calDav)
            {
                ShowDueTime(null);
            }
            _repeat.SetEnabled(!_calDav);
            UpdateClearDue();
            return;
        }
        if (_writable && !_completeOnly && _itemId is not null && _collection is { } from && from.Id != target.Id)
        {
            _ = MoveAsync(from, target);
        }
    }

    private TransferResult? Prepare(Item item, Collection target)
    {
        try
        {
            return TaskTransfer.Move(item, !_calDav, target, !IsCalDav(target), TimeZoneInfo.Local, DateTimeOffset.UtcNow);
        }
        catch (EditRefusedException ex)
        {
            ShowMoveNotice(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Preparing the move of task {ItemId} failed", item.Id);
        }
        return null;
    }

    private void ShowMoveNotice(string message)
    {
        _notice.Severity = InfoBarSeverity.Warning;
        _notice.Message = message;
        _notice.IsOpen = true;
    }

    private async Task MoveAsync(Collection from, Collection target)
    {
        // Typed changes are saved to the task where it is now; the drop-down shows that list until the move is done.
        SelectList(from.Id);
        Save();
        if (_asking || Store is not { } store || _itemId is not { } id || store.GetItem(id) is not { } item)
        {
            return;
        }
        if (Prepare(item, target) is not { } result)
        {
            return;
        }

        if (result.Lost.Count > 0)
        {
            if (!await ConfirmMoveAsync(store, from, target, result))
            {
                return;
            }
            // The task may have changed (a sync) while the dialog was open: move what is stored now, if it loses no more.
            if (store.GetItem(id) is not { } fresh)
            {
                return;
            }
            if (fresh.RawPayload != item.RawPayload)
            {
                item = fresh;
                if (Prepare(item, target) is not { } rebuilt)
                {
                    return;
                }
                if (!rebuilt.Lost.SequenceEqual(result.Lost))
                {
                    ShowMoveNotice("This task changed while you were deciding; try again.");
                    return;
                }
                result = rebuilt;
            }
        }

        string movedId;
        try
        {
            movedId = store.MoveItem(item.Id, result.Moved);
            Log.Information("Task {ItemId} moved to another list as {MovedId}", item.Id, movedId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Moving task {ItemId} failed", item.Id);
            return;
        }
        // The old task is gone; the editor shows the moved one.
        _itemId = null;
        Open(movedId, _all, true);
        Changed?.Invoke();
    }

    private async Task<bool> ConfirmMoveAsync(LocalStore store, Collection from, Collection target, TransferResult result)
    {
        var account = store.GetAccounts().FirstOrDefault(a => a.Id == target.AccountId)?.DisplayName ?? "the other account";
        var content = new SpacedStack { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = $"The task is copied to {target.Name} in {account}, then deleted from {from.Name}.",
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
            var dialog = new ContentDialog(ViewParts.DialogHost)
            {
                Title = $"Move to {target.Name}?",
                Content = content,
                PrimaryButtonText = "Move",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _asking = false;
        }
    }
}
