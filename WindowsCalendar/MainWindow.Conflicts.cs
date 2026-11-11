using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Serilog;
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
        summary.Children.Add(actions);
        return new ListBoxItem
        {
            Content = summary,
            Tag = conflict.OnCalendar ? result : null,
        };
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
                store.DeleteItem(current.Id);
            }
            else
            {
                if (!graph && current.IsRecurring && !await ConfirmOverwriteAsync())
                {
                    ShowConflictList();
                    return;
                }
                var mine = ConflictResolver.UseMine(current, saved.LocalPayload, graph, TimeZoneInfo.Local);
                store.UpdateItem(mine);
                if (current.Kind == ItemKind.Event)
                {
                    _lastChange = (current.Id, current.RawPayload, mine.RawPayload, null);
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

    /// <summary>Sending a whole CalDAV series replaces the server's copy, so it asks first.</summary>
    private async Task<bool> ConfirmOverwriteAsync()
    {
        ConflictPopup.IsOpen = false;
        var dialog = Destructive.Apply(new ContentDialog(DialogHost)
        {
            Title = "Overwrite the server version?",
            Content = "This repeating series is sent as you saved it and replaces the version on the server.",
            PrimaryButtonText = "Overwrite",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        });
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
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
