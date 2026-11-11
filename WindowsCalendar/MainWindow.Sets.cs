using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Serilog;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Views;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;
using ContentDialogButton = Wpf.Ui.Controls.ContentDialogButton;
using ContentDialogResult = Wpf.Ui.Controls.ContentDialogResult;

namespace WindowsCalendar;

/// <summary>
/// Calendar sets in the sidebar (calendar-sets R1, R3, R4, R5): a selector with "All" first, then the user's sets,
/// and Ctrl+1 … Ctrl+9 for the first nine. Switching rewrites the sidebar ticks; a manual tick marks the active set
/// "(modified)". The options menu saves the ticks as a new set or into the active one, or opens Settings.
/// </summary>
public sealed partial class MainWindow
{
    // True while the selector is being filled, so its selection change is not a switch.
    private bool _fillingSets;

    /// <summary>The selector entries in order: "All" (null id), then user-defined sets by position (R1).</summary>
    private static List<(string? Id, string Name)> SetEntries(LocalStore store) =>
        [(null, "All"), .. store.GetCalendarSets().Select(s => ((string?)s.Id, s.Name))];

    /// <summary>Fills the selector, the active set selected and marked "(modified)" while the ticks differ (R5).</summary>
    private void ShowSets(bool hasAccounts)
    {
        SetBar.Visibility = hasAccounts && Store is not null ? Visibility.Visible : Visibility.Collapsed;
        if (Store is not { } store)
        {
            return;
        }
        var active = store.ActiveSetId;
        var modified = store.IsActiveSetModified();
        _fillingSets = true;
        SetSelector.Items.Clear();
        foreach (var (id, name) in SetEntries(store))
        {
            var item = new ComboBoxItem { Content = id == active && modified ? $"{name} (modified)" : name, Tag = id };
            SetSelector.Items.Add(item);
            if (id == active)
            {
                SetSelector.SelectedItem = item;
            }
        }
        _fillingSets = false;
    }

    private void OnSetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingSets || SetSelector.SelectedItem is not ComboBoxItem item)
        {
            return;
        }
        // After the selection change completes, since switching refills the selector.
        var id = (string?)item.Tag;
        Dispatcher.BeginInvoke(() => SwitchSet(id));
    }

    /// <summary>Ctrl+1 … Ctrl+9 (R4): the entry at <paramref name="index"/>; false when there is none.</summary>
    private bool SwitchSetAt(int index)
    {
        if (Store is not { } store || SetEntries(store) is not { } entries || index >= entries.Count)
        {
            return false;
        }
        SwitchSet(entries[index].Id);
        return true;
    }

    /// <summary>Shows the set's members only (R5, R6); the tray flyout reads the ticks when it next opens.</summary>
    private void SwitchSet(string? setId)
    {
        Store?.SwitchToSet(setId);
        ShowAccounts();
        RenderView();
    }

    /// <summary>The ⋯ menu (R3): Save as new set, Update the active set while it is modified, Manage sets.</summary>
    private void OnSetMenu(object sender, RoutedEventArgs e)
    {
        if (Store is not { } store)
        {
            return;
        }
        var menu = new ContextMenu { PlacementTarget = SetMenuButton, Placement = PlacementMode.Bottom };
        var save = new MenuItem { Header = "Save as new set…" };
        save.Click += async (_, _) => await SaveAsNewSetAsync();
        menu.Items.Add(save);
        if (store.ActiveSetId is { } active && store.IsActiveSetModified()
            && store.GetCalendarSets().FirstOrDefault(s => s.Id == active) is { } set)
        {
            var update = new MenuItem { Header = $"Update '{set.Name}'" };
            update.Click += (_, _) =>
            {
                store.UpdateCalendarSet(set.Id);
                ShowSets(true);
            };
            menu.Items.Add(update);
        }
        var manage = new MenuItem { Header = "Manage sets…" };
        manage.Click += (_, _) => OpenSettings("Calendar sets");
        menu.Items.Add(manage);
        menu.IsOpen = true;
    }

    /// <summary>Asks for a name, then saves the current ticks as a new set, which becomes the active one (R3).</summary>
    private async Task SaveAsNewSetAsync()
    {
        var name = new Wpf.Ui.Controls.TextBox { PlaceholderText = "For example, Work" };
        var dialog = new ContentDialog(DialogHost)
        {
            Title = "Save as new set",
            Content = ViewParts.Field("Name", name),
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
        name.Loaded += (_, _) => name.Focus();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(name.Text) || Store is not { } store)
        {
            return;
        }
        var set = store.CreateCalendarSet(name.Text);
        Log.Information("Calendar set {SetId} created", set.Id);
        ShowSets(true);
    }
}
