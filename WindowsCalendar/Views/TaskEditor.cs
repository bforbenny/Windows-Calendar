using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Serilog;
using Windows.System;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Views;

/// <summary>
/// The task editor side panel (tasks interim), in the event editor's place, with the same auto-save: text when it
/// loses focus (or Enter in the title), the checkbox and pickers at once. Fields: title, completed, due date (date
/// only), importance, notes. Only To Do tasks in writable lists can be changed; a recurring one can only be completed
/// or reopened. Radicale tasks and Flagged emails are view-only.
/// </summary>
internal sealed class TaskEditor : UserControl
{
    private readonly TextBlock _heading = new() { Text = "Task", Style = Theme.Style("SubtitleTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _delete = IconButton("", "Delete");
    private readonly InfoBar _notice = new() { IsClosable = false, Severity = InfoBarSeverity.Informational };
    private readonly TextBox _title = new() { PlaceholderText = "Add a title", Header = "Title" };
    private readonly CheckBox _completed = new() { Content = "Completed" };
    private readonly CalendarDatePicker _due = new() { Header = "Due", PlaceholderText = "No due date", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _clearDue = new() { Content = "Clear", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly ComboBox _importance = new() { Header = "Importance", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _list = new() { Foreground = Theme.Brush("TextSecondaryBrush") };
    private readonly TextBox _notes = new() { Header = "Notes", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120 };

    private static readonly string[] Importances = ["low", "normal", "high"];

    private string? _itemId;
    private bool _writable;
    private bool _completeOnly;
    private bool _loading;

    public TaskEditor()
    {
        var close = IconButton("", "Close (Esc)");
        close.Click += (_, _) => Close();
        _delete.Click += async (_, _) => await DeleteAsync();

        var header = new Grid { ColumnSpacing = 4, Margin = new Thickness(16, 12, 8, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_heading);
        Grid.SetColumn(_delete, 1);
        Grid.SetColumn(close, 2);
        header.Children.Add(_delete);
        header.Children.Add(close);

        foreach (var name in new[] { "Low", "Normal", "High" })
        {
            _importance.Items.Add(name);
        }
        var dueRow = new Grid { ColumnSpacing = 8 };
        dueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_clearDue, 1);
        dueRow.Children.Add(_due);
        dueRow.Children.Add(_clearDue);

        var form = new StackPanel { Spacing = 12, Padding = new Thickness(16, 0, 16, 16) };
        form.Children.Add(_notice);
        form.Children.Add(_title);
        form.Children.Add(_completed);
        form.Children.Add(dueRow);
        form.Children.Add(_importance);
        form.Children.Add(_list);
        form.Children.Add(_notes);

        var root = new Grid { RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var scroll = new ScrollViewer { Content = form };
        Grid.SetRow(scroll, 1);
        root.Children.Add(header);
        root.Children.Add(scroll);
        Content = root;
        Width = EventEditor.PanelWidth;
        Visibility = Visibility.Collapsed;

        _title.LostFocus += (_, _) => Save();
        _notes.LostFocus += (_, _) => Save();
        _title.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                Save();
            }
        };
        _completed.Click += (_, _) => Save();
        _due.DateChanged += (_, _) => Save();
        _clearDue.Click += (_, _) => _due.Date = null;
        _importance.SelectionChanged += (_, _) => Save();

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            if (IsOpen)
            {
                e.Handled = true;
                Close();
            }
        };
        KeyboardAccelerators.Add(escape);
    }

    /// <summary>Raised after a change is saved to the store, so the views redraw.</summary>
    public event Action? Changed;

    /// <summary>Raised when the panel opens or closes.</summary>
    public event Action? OpenChanged;

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>The task that is open.</summary>
    public string? ItemId => _itemId;

    private static LocalStore? Store => ((App)Application.Current).Store;

    /// <summary>
    /// Opens a task; the one being edited is saved first. <paramref name="writable"/> is false for Radicale tasks and
    /// read-only lists.
    /// </summary>
    public void Open(string itemId, IReadOnlyDictionary<string, Collection> collections, bool writable)
    {
        Save();
        if (Store?.GetItem(itemId) is not { } item || !collections.TryGetValue(item.CollectionId, out var list))
        {
            return;
        }
        _itemId = itemId;
        _writable = writable;
        _completeOnly = writable && item.IsRecurring;
        Show(item, list);
        var message = list.ReadOnly ? "This task list is read-only."
            : !writable ? "Editing tasks in this account comes later."
            : _completeOnly ? "This task repeats. It can be completed here, but not changed." : "";
        _notice.Message = message;
        _notice.IsOpen = message.Length > 0;
        if (!IsOpen)
        {
            Visibility = Visibility.Visible;
            OpenChanged?.Invoke();
        }
    }

    /// <summary>After a change made outside the panel (a tick in the list or calendar, a sync): shows the stored task.</summary>
    public void Reload(string itemId, IReadOnlyDictionary<string, Collection> collections)
    {
        if (IsOpen && _itemId == itemId && Store?.GetItem(itemId) is { } item && collections.TryGetValue(item.CollectionId, out var list))
        {
            Show(item, list);
        }
    }

    /// <summary>Saves what is typed and closes.</summary>
    public void Close()
    {
        Save();
        Hide();
    }

    /// <summary>Closes without saving, e.g. when the task was deleted.</summary>
    public void Hide()
    {
        _itemId = null;
        if (IsOpen)
        {
            Visibility = Visibility.Collapsed;
            OpenChanged?.Invoke();
        }
    }

    private void Show(Item item, Collection list)
    {
        _loading = true;
        var fields = TaskQuery.Fields(item, TimeZoneInfo.Local);
        _title.Text = fields.Title;
        _completed.IsChecked = fields.Completed;
        _due.Date = fields.Due is { } due ? new DateTimeOffset(due.ToDateTime(TimeOnly.MinValue)) : null;
        _importance.SelectedIndex = Array.IndexOf(Importances, fields.Importance);
        _notes.Text = (fields.Notes ?? "").Replace("\r\n", "\r").Replace("\n", "\r");
        _list.Text = $"List: {list.Name}";

        var editable = _writable && !_completeOnly;
        _title.IsReadOnly = _notes.IsReadOnly = !editable;
        _due.IsEnabled = _clearDue.IsEnabled = _importance.IsEnabled = editable;
        _completed.IsEnabled = _writable;
        _delete.Visibility = _writable ? Visibility.Visible : Visibility.Collapsed;
        _loading = false;
    }

    private TaskFields ReadFields(TaskFields old)
    {
        var title = _title.Text.Trim();
        return new TaskFields(
            title.Length > 0 ? title : old.Title,
            _completed.IsChecked == true,
            _due.Date is { } d ? DateOnly.FromDateTime(d.Date) : null,
            _importance.SelectedIndex >= 0 ? Importances[_importance.SelectedIndex] : old.Importance,
            _notes.Text.Replace("\r\n", "\n").Replace("\r", "\n"));
    }

    private void Save()
    {
        if (_loading || !_writable || !IsOpen || _itemId is not { } id || Store is not { } store || store.GetItem(id) is not { } item)
        {
            return;
        }
        try
        {
            var old = GraphTaskMapper.Read(item);
            var fields = _completeOnly ? old with { Completed = _completed.IsChecked == true } : ReadFields(old);
            if (GraphTasks.Edit(item, fields, TimeZoneInfo.Local) is { } edited)
            {
                store.UpdateItem(edited);
                Log.Information("Task {ItemId} saved", id);
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving task {ItemId} failed", id);
        }
    }

    private async System.Threading.Tasks.Task DeleteAsync()
    {
        if (_itemId is not { } id || Store is not { } store)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete this task?",
            Content = string.IsNullOrWhiteSpace(_title.Text) ? "(No title)" : _title.Text,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        try
        {
            store.DeleteItem(id);
            Log.Information("Task {ItemId} deleted", id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Deleting task {ItemId} failed", id);
        }
        Hide();
        Changed?.Invoke();
    }

    private static Button IconButton(string glyph, string name)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Padding = new Thickness(8),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        return button;
    }
}
