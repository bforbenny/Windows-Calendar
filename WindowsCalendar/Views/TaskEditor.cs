using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Serilog;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;
using ContentDialogButton = Wpf.Ui.Controls.ContentDialogButton;
using ContentDialogResult = Wpf.Ui.Controls.ContentDialogResult;
using InfoBar = Wpf.Ui.Controls.InfoBar;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;
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
    private readonly Button _delete = ViewParts.IconButton("", "Delete");
    private readonly InfoBar _notice = new() { IsClosable = false, Severity = InfoBarSeverity.Informational };
    private readonly TextBox _title = new Wpf.Ui.Controls.TextBox { PlaceholderText = "Add a title" };
    private readonly CheckBox _completed = new() { Content = "Completed" };
    private readonly DatePicker _due = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _clearDue = new() { Content = "Clear", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly ComboBox _importance = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _list = new() { Foreground = Theme.Brush("TextSecondaryBrush") };
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120 };

    private static readonly string[] Importances = ["low", "normal", "high"];

    private string? _itemId;
    // A new task not yet created: the list it goes to (created once it has a title).
    private Collection? _newList;
    private bool _writable;
    private bool _completeOnly;
    private bool _loading;

    private readonly Button _switch;

    public TaskEditor()
    {
        _switch = ViewParts.KindSwitch("New task", "New event", () => SwitchRequested?.Invoke(_title.Text), out _);
        var close = ViewParts.IconButton("", "Close (Esc)");
        close.Click += (_, _) => Close();
        _delete.Click += async (_, _) => await DeleteAsync();

        var header = new Grid { Margin = new Thickness(16, 12, 8, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_heading);
        header.Children.Add(_switch);
        Grid.SetColumn(_delete, 1);
        Grid.SetColumn(close, 2);
        _delete.Margin = close.Margin = new Thickness(4, 0, 0, 0);
        _delete.Margin = close.Margin = new Thickness(4, 0, 0, 0);
        header.Children.Add(_delete);
        header.Children.Add(close);

        foreach (var name in new[] { "Low", "Normal", "High" })
        {
            _importance.Items.Add(name);
        }
        var dueRow = new Grid();
        _clearDue.Margin = new Thickness(8, 0, 0, 0);
        dueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_clearDue, 1);
        dueRow.Children.Add(_due);
        dueRow.Children.Add(_clearDue);

        var form = new SpacedStack { Spacing = 12, Margin = new Thickness(16, 0, 16, 16) };
        form.Children.Add(_notice);
        form.Children.Add(ViewParts.Field("Title", _title));
        form.Children.Add(_completed);
        form.Children.Add(ViewParts.Field("Due", dueRow));
        form.Children.Add(ViewParts.Field("Importance", _importance));
        form.Children.Add(_list);
        form.Children.Add(ViewParts.Field("Notes", _notes));

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
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
            if (e.Key == Key.Enter)
            {
                Save();
            }
        };
        _completed.Click += (_, _) => Save();
        _due.SelectedDateChanged += (_, _) => Save();
        _clearDue.Click += (_, _) => _due.SelectedDate = null;
        _importance.SelectionChanged += (_, _) => Save();

        // Esc closes; an open drop-down closes first, as it handles its own Esc.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && IsOpen)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    /// <summary>Raised after a change is saved to the store, so the views redraw.</summary>
    public event Action? Changed;

    /// <summary>Raised when the panel opens or closes.</summary>
    public event Action? OpenChanged;

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Raised with the typed title when a new task is switched to a new event from its heading.</summary>
    public event Action<string>? SwitchRequested;

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
        _heading.Text = "Task";
        ShowSwitch(false);
        _newList = null;
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

    /// <summary>Opens a blank task for <paramref name="list"/> (tasks interim); it is created once it has a title.</summary>
    public void OpenNew(Collection list, string title = "")
    {
        Save();
        _heading.Text = "New task";
        ShowSwitch(true);
        _itemId = null;
        _newList = list;
        _writable = true;
        _completeOnly = false;
        Show(new TaskFields(title, false, null, "normal", null), list);
        _delete.Visibility = Visibility.Collapsed;
        _notice.IsOpen = false;
        if (!IsOpen)
        {
            Visibility = Visibility.Visible;
            OpenChanged?.Invoke();
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _title.Focus());
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

    /// <summary>A new task shows the New task / New event drop-down in place of its heading.</summary>
    private void ShowSwitch(bool isNew)
    {
        _switch.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
        _heading.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Closes without saving, e.g. when the task was deleted.</summary>
    public void Hide()
    {
        _itemId = null;
        _newList = null;
        if (IsOpen)
        {
            Visibility = Visibility.Collapsed;
            OpenChanged?.Invoke();
        }
    }

    private void Show(Item item, Collection list) => Show(TaskQuery.Fields(item, TimeZoneInfo.Local), list);

    private void Show(TaskFields fields, Collection list)
    {
        _loading = true;
        _title.Text = fields.Title;
        _completed.IsChecked = fields.Completed;
        _due.SelectedDate = fields.Due?.ToDateTime(TimeOnly.MinValue);
        _importance.SelectedIndex = Array.IndexOf(Importances, fields.Importance);
        _notes.Text = (fields.Notes ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        // With its source (calendar-views interim); the whole line is already muted.
        _list.Text = Store?.GetAccounts().FirstOrDefault(a => a.Id == list.AccountId) is { } account
            ? $"List: {list.Name}  {CollectionSource.For(account)}"
            : $"List: {list.Name}";

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
            _due.SelectedDate is { } d ? DateOnly.FromDateTime(d.Date) : null,
            _importance.SelectedIndex >= 0 ? Importances[_importance.SelectedIndex] : old.Importance,
            _notes.Text.Replace("\r\n", "\n").Replace("\r", "\n"));
    }

    private void Save()
    {
        if (!_loading && IsOpen && _newList is { } newList)
        {
            Create(newList);
            return;
        }
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

    /// <summary>Creates the new task once it has a title; from then on the panel edits it like any other.</summary>
    private void Create(Collection list)
    {
        if (_title.Text.Trim().Length == 0 || Store is not { } store)
        {
            return;
        }
        try
        {
            var task = GraphTasks.Create(list, ReadFields(new TaskFields("", false, null, "normal", null)), TimeZoneInfo.Local);
            store.CreateItem(task);
            Log.Information("Task {ItemId} created", task.Id);
            _newList = null;
            _itemId = task.Id;
            _heading.Text = "Task";
            ShowSwitch(false);
            _delete.Visibility = Visibility.Visible;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Creating a task failed");
        }
    }

    private async System.Threading.Tasks.Task DeleteAsync()
    {
        if (_itemId is not { } id || Store is not { } store)
        {
            return;
        }
        var dialog = Destructive.Apply(new ContentDialog(ViewParts.DialogHost)
        {
            Title = "Delete this task?",
            Content = string.IsNullOrWhiteSpace(_title.Text) ? "(No title)" : _title.Text,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        });
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

}
