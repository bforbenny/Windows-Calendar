using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Serilog;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Sync;
using WindowsCalendar.Views;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;
using ContentDialogButton = Wpf.Ui.Controls.ContentDialogButton;
using ContentDialogResult = Wpf.Ui.Controls.ContentDialogResult;

namespace WindowsCalendar;

/// <summary>
/// The conflicts list (conflict-resolution R5 interim): clicking the "N conflicts" banner or its Show button lists the
/// kept conflict copies, newest first. A row whose item is on the calendar opens it like a search result; a row whose
/// item is gone is greyed. Each row says what differs and offers Keep server (discard the saved copy) and, when the item
/// is still on the calendar, Use mine (send the saved version again, with Undo) (conflict-resolution R6, second slice).
/// </summary>
public sealed partial class MainWindow
{
    private void OnConflictBarClicked(object sender, MouseButtonEventArgs e) => ShowConflictList();

    private void OnShowConflicts(object sender, RoutedEventArgs e) => ShowConflictList();

    private void ShowConflictList()
    {
        if (Store is not { } store)
        {
            return;
        }
        KeyboardNavigation.SetTabNavigation(ConflictList, KeyboardNavigationMode.Continue);
        ConflictList.Items.Clear();
        foreach (var conflict in store.GetConflicts())
        {
            ConflictList.Items.Add(ConflictRow(conflict));
        }
        if (ConflictList.Items.Count == 0)
        {
            ConflictList.Items.Add(NoteRow("No conflicts"));
        }
        ConflictPopup.IsOpen = true;
        if (ConflictList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.IsEnabled && i.Focusable) is { } first)
        {
            ConflictList.SelectedItem = first;
            first.Focus();
        }
    }

    /// <summary>
    /// The search row layout, with what happened as its second line, the first differences as a third and the actions
    /// below; greyed and not clickable when the item is gone.
    /// </summary>
    private ListBoxItem ConflictRow(ConflictInfo conflict)
    {
        var what = !conflict.OnCalendar ? "No longer on the calendar"
            : conflict.LocalDeleted ? "Your delete was not applied"
            : "Changed on both";
        var result = new SearchResult(conflict.ItemId, conflict.CollectionId, conflict.Kind, conflict.Title, what,
            conflict.StartUtc, null, conflict.AllDay, null, false, false);
        if (!_collections.TryGetValue(conflict.CollectionId, out var collection))
        {
            return NoteRow($"{conflict.Title} · {what}");
        }
        var summary = new SpacedStack { Spacing = 4 };
        var info = ResultRow(result, collection);
        info.Opacity = conflict.OnCalendar ? 1 : 0.6;
        summary.Children.Add(info);
        if (DifferenceSummary(conflict, collection) is { } differences)
        {
            var line = Caption(differences);
            line.TextWrapping = TextWrapping.Wrap;
            line.Margin = new Thickness(26, 0, 0, 0);
            summary.Children.Add(line);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(26, 2, 0, 4) };
        actions.Children.Add(ConflictButton("Keep server", conflict, mine: false));
        if (conflict.OnCalendar)
        {
            actions.Children.Add(ConflictButton("Use mine", conflict, mine: true));
        }
        if (!conflict.OnCalendar && !conflict.LocalDeleted)
        {
            actions.Children.Add(RestoreButton(conflict));
        }
        summary.Children.Add(actions);
        return new ListBoxItem
        {
            Content = summary,
            Tag = conflict.OnCalendar ? result : null,
        };
    }

    /// <summary>Restore (conflict-resolution R3): for a row whose item is gone, saves the local version as a new item.</summary>
    private Button RestoreButton(ConflictInfo conflict)
    {
        var button = new Wpf.Ui.Controls.Button
        {
            Content = "Restore",
            Margin = new Thickness(0, 0, 8, 0),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, $"Restore, {conflict.Title}");
        button.Click += (_, e) =>
        {
            e.Handled = true;
            _ = RestoreAsync(conflict);
        };
        return button;
    }

    private Button ConflictButton(string text, ConflictInfo conflict, bool mine)
    {
        var button = new Wpf.Ui.Controls.Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 8, 0),
            Appearance = mine ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, $"{text}, {conflict.Title}");
        button.Click += (_, e) =>
        {
            e.Handled = true;
            if (mine)
            {
                _ = UseMineAsync(conflict);
            }
            else
            {
                KeepServer(conflict);
            }
        };
        return button;
    }

    /// <summary>The first two differences as "Field: yours, server", then "+N more"; null when there are none.</summary>
    private string? DifferenceSummary(ConflictInfo conflict, Collection collection)
    {
        if (!conflict.OnCalendar || conflict.LocalDeleted || Store is not { } store)
        {
            return null;
        }
        try
        {
            if (store.GetConflict(conflict.Id) is not { LocalPayload: { } local } || store.GetItem(conflict.ItemId) is not { } current)
            {
                return null;
            }
            var differences = ConflictResolver.Differences(local, current, IsGraph(collection), TimeZoneInfo.Local);
            if (differences.Count == 0)
            {
                return null;
            }
            var text = string.Join(" · ", differences.Take(2).Select(d => $"{d.Field}: {d.Yours} (yours), {d.Server} (server)"));
            return differences.Count > 2 ? $"{text} · +{differences.Count - 2} more" : text;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Summarising conflict {Id} failed", conflict.Id);
            return null;
        }
    }

    /// <summary>Keep server: the server version stays and the saved copy is discarded.</summary>
    private void KeepServer(ConflictInfo conflict)
    {
        if (Store is not { } store)
        {
            return;
        }
        try
        {
            store.DeleteConflict(conflict.Id);
            Log.Information("Conflict {Id} resolved: keep server", conflict.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Resolving conflict {Id} failed", conflict.Id);
        }
        AfterConflictAction();
    }

    /// <summary>
    /// Use mine: the saved version is sent again over the server one (or the delete is queued again), after asking for a
    /// CalDAV repeating series. An edit offers Undo like any event change.
    /// </summary>
    private async Task UseMineAsync(ConflictInfo conflict)
    {
        if (Store is not { } store)
        {
            return;
        }
        try
        {
            if (store.GetConflict(conflict.Id) is not { } saved || store.GetItem(conflict.ItemId) is not { } current
                || !_collections.TryGetValue(current.CollectionId, out var collection))
            {
                AfterConflictAction();
                return;
            }
            var graph = IsGraph(collection);
            if (saved.LocalPayload is null)
            {
                using (store.BeginUndo("Conflict: your delete"))
                {
                    store.DeleteItem(current.Id);
                }
            }
            else
            {
                if (!graph && current.IsRecurring && !await ConfirmOverwriteAsync(
                    ConflictResolver.Differences(saved.LocalPayload, current, false, TimeZoneInfo.Local),
                    ConflictResolver.SeriesOccurrenceDifferences(saved.LocalPayload, current.RawPayload, TimeZoneInfo.Local)))
                {
                    ShowConflictList();
                    return;
                }
                var mine = ConflictResolver.UseMine(current, saved.LocalPayload, graph, TimeZoneInfo.Local);
                using (store.BeginUndo("Conflict: your version"))
                {
                    store.UpdateItem(mine);
                }
                if (current.Kind == ItemKind.Event)
                {
                    ShowUndo("Your version used", canUndo: true);
                }
            }
            store.DeleteConflict(conflict.Id);
            Log.Information("Conflict {Id} resolved: use mine", conflict.Id);
            RenderView();
            _editor.Reload(current.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Resolving conflict {Id} failed", conflict.Id);
        }
        AfterConflictAction();
    }

    /// <summary>
    /// Sending a whole CalDAV series replaces the server's copy, so it lists what that overwrites first (conflict-resolution
    /// R6): the series fields that differ and the occurrences (overrides, deleted dates) that differ.
    /// </summary>
    private async Task<bool> ConfirmOverwriteAsync(IReadOnlyList<FieldDifference> fields, IReadOnlyList<string> occurrences)
    {
        ConflictPopup.IsOpen = false;
        var content = new SpacedStack { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "This repeating series is sent as you saved it and replaces the version on the server.",
            TextWrapping = TextWrapping.Wrap,
        });
        var lines = fields.Select(d => $"{d.Field}: {d.Yours} (yours), {d.Server} (server)").Concat(occurrences).ToList();
        if (lines.Count > 0)
        {
            content.Children.Add(new TextBlock { Text = "This overwrites:", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new ScrollViewer
            {
                MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock { Text = string.Join("\n", lines.Select(l => "• " + l)), TextWrapping = TextWrapping.Wrap },
            });
        }
        var dialog = Destructive.Apply(new ContentDialog(DialogHost)
        {
            Title = "Use your version?",
            Content = content,
            PrimaryButtonText = "Use mine",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        });
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Restore as new (conflict-resolution R3): asks which writable calendar (or task list) to restore into, the original
    /// pre-selected, creates the saved version there, then removes the conflict copy (no undo). What the target
    /// cannot take along is listed first; a Microsoft 365 meeting is refused.
    /// </summary>
    private async Task RestoreAsync(ConflictInfo conflict)
    {
        if (Store is not { } store)
        {
            return;
        }
        try
        {
            if (store.GetConflict(conflict.Id) is not { LocalPayload: { } payload } saved)
            {
                AfterConflictAction();
                return;
            }
            var kind = saved.Kind == ItemKind.Task ? CollectionKind.TaskList : CollectionKind.Calendar;
            var fromGraph = _collections.TryGetValue(saved.CollectionId, out var original)
                ? IsGraph(original) : payload.TrimStart().StartsWith('{');
            var accounts = store.GetAccounts();
            var choices = _collections.Values
                .Where(c => c.Kind == kind && !c.ReadOnly && c.SourceUrl is null && accounts.Any(a => a.Id == c.AccountId)).ToList();
            if (choices.Count == 0)
            {
                ShowUndo("Can't restore this item", canUndo: false, "There is no calendar or list to restore it into.");
                return;
            }

            ConflictPopup.IsOpen = false;
            var picker = new ComboBox { MinWidth = 280 };
            foreach (var c in choices)
            {
                var source = accounts.FirstOrDefault(a => a.Id == c.AccountId) is { } account ? "  " + CollectionSource.For(account) : "";
                picker.Items.Add(new ComboBoxItem { Content = c.Name + source, Tag = c });
            }
            picker.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Id == saved.CollectionId));
            var ask = new SpacedStack { Spacing = 8 };
            ask.Children.Add(new TextBlock
            {
                Text = kind == CollectionKind.Calendar ? "Restore this event as a new event in:" : "Restore this task as a new task in:",
                TextWrapping = TextWrapping.Wrap,
            });
            ask.Children.Add(picker);
            var pick = new ContentDialog(DialogHost)
            {
                Title = kind == CollectionKind.Calendar ? "Restore to which calendar?" : "Restore to which list?",
                Content = ask,
                PrimaryButtonText = "Restore",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await pick.ShowAsync() != ContentDialogResult.Primary || (picker.SelectedItem as ComboBoxItem)?.Tag is not Collection target)
            {
                ShowConflictList();
                return;
            }

            TransferResult result;
            try
            {
                var source = ConflictResolver.RestoreSource(payload, saved.ItemId, saved.CollectionId, saved.Kind, fromGraph, TimeZoneInfo.Local);
                result = saved.Kind == ItemKind.Task
                    ? TaskTransfer.Move(source, fromGraph, target, IsGraph(target), TimeZoneInfo.Local, DateTimeOffset.UtcNow)
                    : EventTransfer.Move(source, fromGraph, target, IsGraph(target), TimeZoneInfo.Local, DateTimeOffset.UtcNow);
            }
            catch (EditRefusedException ex)
            {
                ShowUndo("Can't restore this item", canUndo: false, ex.Message);
                ShowConflictList();
                return;
            }
            if (result.Lost.Count > 0)
            {
                var lost = new SpacedStack { Spacing = 8 };
                lost.Children.Add(new TextBlock { Text = "It can't take along:", TextWrapping = TextWrapping.Wrap });
                lost.Children.Add(new TextBlock { Text = string.Join("\n", result.Lost.Select(l => "• " + l)), TextWrapping = TextWrapping.Wrap });
                var confirm = new ContentDialog(DialogHost)
                {
                    Title = $"Restore to {target.Name}?",
                    Content = lost,
                    PrimaryButtonText = "Restore",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                {
                    ShowConflictList();
                    return;
                }
            }

            // Not undoable (undo out of scope: conflict choices): undoing the create would leave no copy at all, as the
            // conflict copy is removed here.
            store.CreateItem(result.Moved);
            store.DeleteConflict(conflict.Id);
            ShowUndo("Restored", canUndo: false, $"Added to {target.Name}.");
            Log.Information("Conflict {Id} resolved: restored as {ItemId}", conflict.Id, result.Moved.Id);
            RenderView();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Restoring conflict {Id} failed", conflict.Id);
        }
        AfterConflictAction();
    }

    /// <summary>Refreshes the banner and the list after a conflict is resolved; the popup closes when none are left.</summary>
    private void AfterConflictAction()
    {
        ShowConflicts();
        if ((Store?.GetConflictCount() ?? 0) == 0)
        {
            ConflictPopup.IsOpen = false;
        }
        else
        {
            ShowConflictList();
        }
    }

    private void OpenConflictRow(ListBoxItem item)
    {
        ConflictPopup.IsOpen = false;
        OpenResult((SearchResult)item.Tag);
    }

    private void OnConflictRowClicked(object sender, MouseButtonEventArgs e)
    {
        // A Run in a row is not a Visual, so walk its logical parent instead.
        for (var hit = e.OriginalSource as DependencyObject; hit is not null;
            hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit))
        {
            if (hit is ButtonBase)
            {
                return;
            }
            if (hit is ListBoxItem { Tag: SearchResult } item)
            {
                OpenConflictRow(item);
                return;
            }
        }
    }

    private void OnConflictListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is not ButtonBase && ConflictList.SelectedItem is ListBoxItem { Tag: SearchResult } item)
        {
            e.Handled = true;
            OpenConflictRow(item);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ConflictPopup.IsOpen = false;
        }
    }
}
