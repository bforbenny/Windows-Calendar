using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Serilog;
using WindowsCalendar.Core.CalDav;
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
/// only), importance, repeat (the event editor's Repeat panel, saved at once; tasks R7b), reminder (date and time;
/// reminders-notifications R5a), notes, list (every writable task list, to move the task: tasks R11); Radicale tasks add an optional due time and show Repeat disabled (tasks third
/// slice). Only tasks in writable lists can be changed; a To Do task whose repeat pattern the panel can't show can
/// only be completed or reopened. Flagged emails are view-only.
/// </summary>
internal sealed partial class TaskEditor : UserControl
{
    private readonly TextBlock _heading = new() { Text = "Task", Style = Theme.Style("SubtitleTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _delete = ViewParts.IconButton("", "Delete");
    private readonly InfoBar _notice = new() { IsClosable = false, Severity = InfoBarSeverity.Informational };
    private readonly TextBox _title = new Wpf.Ui.Controls.TextBox { PlaceholderText = "Add a title" };
    private readonly CheckBox _completed = new() { Content = "Completed" };
    private readonly DatePicker _due = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _clearDue = new() { Content = "Clear", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly ComboBox _importance = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly DatePicker _reminderDate = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TimeBox _reminderTime = new() { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _clearReminder = new() { Content = "Clear", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 0) };
    private readonly StackPanel _reminderField;
    private readonly RepeatPanel _repeat = new();
    private readonly TimeBox _dueTimeBox = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button _addTime = new() { Content = "Add time" };
    private readonly Button _clearTime = new() { Content = "Clear", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 0) };
    private readonly StackPanel _timeField;
    private readonly TextBlock _dueTime = new() { Foreground = Theme.Brush("TextSecondaryBrush"), Visibility = Visibility.Collapsed };
    private readonly TextBlock _repeatNote = new()
    {
        Text = "This task repeats in a way that can't be shown here.",
        Foreground = Theme.Brush("TextSecondaryBrush"),
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };
    private readonly ComboBox _listBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120 };

    private static readonly string[] Importances = ["low", "normal", "high"];

    private string? _itemId;
    // A new task not yet created: the list it goes to (created once it has a title).
    private Collection? _newList;
    private bool _writable;
    // The open task is in a Radicale list (else To Do), and the list it is in.
    private bool _calDav;
    private Collection? _collection;
    private bool _completeOnly;
    private bool _loading;
    // The task repeats but the Repeat panel can't show its pattern.
    private bool _unshownRepeat;
    // The stored item repeats (a Radicale task's repeat is shown disabled, not edited).
    private bool _recurring;

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

        // Reminder: a date and time; no date is no reminder. Radicale tasks keep their alarms, which fire but are not shown here.
        var reminderRow = new Grid();
        reminderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        reminderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        reminderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_reminderTime, 1);
        Grid.SetColumn(_clearReminder, 2);
        reminderRow.Children.Add(_reminderDate);
        reminderRow.Children.Add(_reminderTime);
        reminderRow.Children.Add(_clearReminder);
        _reminderField = ViewParts.Field("Reminder", reminderRow);

        // Due time (Radicale only): optional; no time keeps the due date a plain date.
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal };
        timeRow.Children.Add(_addTime);
        timeRow.Children.Add(_dueTimeBox);
        timeRow.Children.Add(_clearTime);
        _timeField = ViewParts.Field("Due time", timeRow);

        var form = new SpacedStack { Spacing = 12, Margin = new Thickness(16, 0, 16, 16) };
        form.Children.Add(_notice);
        form.Children.Add(ViewParts.Field("Title", _title));
        form.Children.Add(_completed);
        form.Children.Add(ViewParts.Field("Due", dueRow));
        form.Children.Add(_dueTime);
        form.Children.Add(_timeField);
        form.Children.Add(ViewParts.Field("Importance", _importance));
        form.Children.Add(_repeat);
        form.Children.Add(_repeatNote);
        form.Children.Add(_reminderField);
        form.Children.Add(ViewParts.Field("List", _listBox));
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
        _due.SelectedDateChanged += (_, _) =>
        {
            if (!_loading)
            {
                _repeat.SetStart(DueOrToday());
                if (_due.SelectedDate is null)
                {
                    ShowDueTime(null);
                }
                else
                {
                    UpdateTimeButtons();
                }
            }
            Save();
        };
        _addTime.Click += (_, _) =>
        {
            ShowDueTime(new TimeOnly(9, 0));
            Save();
        };
        _clearTime.Click += (_, _) =>
        {
            ShowDueTime(null);
            Save();
        };
        _dueTimeBox.TimeChanged += Save;
        _repeat.RuleChanged += () =>
        {
            UpdateClearDue();
            Save();
        };
        ToolTipService.SetShowOnDisabled(_clearDue, true);
        _clearDue.Click += (_, _) => _due.SelectedDate = null;
        _importance.SelectionChanged += (_, _) => Save();
        _listBox.SelectionChanged += (_, _) => ListSelectionChanged();
        _listBox.DropDownOpened += (_, _) => _listCancelled = false;
        _listBox.DropDownClosed += (_, _) => ListDropDownClosed();
        _listBox.PreviewKeyDown += (_, e) => _listCancelled |= e.Key == Key.Escape && _listBox.IsDropDownOpen;
        _reminderDate.SelectedDateChanged += (_, _) => Save();
        _reminderTime.TimeChanged += Save;
        _clearReminder.Click += (_, _) => _reminderDate.SelectedDate = null;

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

    /// <summary>
    /// Raised with the banner title after a change that offers Undo (a delete, a move to another list); other edits
    /// here are undo steps without a banner (undo R3).
    /// </summary>
    public event Action<string>? UndoOffered;

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
        _all = collections;
        _heading.Text = "Task";
        ShowSwitch(false);
        _newList = null;
        _itemId = itemId;
        _writable = writable;
        _collection = list;
        _calDav = IsCalDav(list);
        _completeOnly = writable && !_calDav && item.IsRecurring && GraphTaskMapper.Read(item).Repeat is null;
        Show(item, list);
        var message = list.ReadOnly ? "This task list is read-only."
            : !writable ? "This task can't be changed here."
            : _completeOnly ? "This task repeats in a way that can't be shown here. It can be completed, but not changed." : "";
        _notice.Severity = InfoBarSeverity.Informational;
        _notice.Message = message;
        _notice.IsOpen = message.Length > 0;
        if (!IsOpen)
        {
            Visibility = Visibility.Visible;
            OpenChanged?.Invoke();
        }
    }

    /// <summary>
    /// Opens a blank task for <paramref name="list"/> (tasks interim); it is created once it has a title. Quick add's
    /// "More details" passes the due date and repeat it understood (quick-add R4).
    /// </summary>
    public void OpenNew(Collection list, string title = "", DateOnly? due = null, RepeatRule? repeat = null)
    {
        Save();
        _heading.Text = "New task";
        ShowSwitch(true);
        _itemId = null;
        _newList = list;
        _writable = true;
        _collection = list;
        _calDav = IsCalDav(list);
        _completeOnly = false;
        _unshownRepeat = _recurring = false;
        _reminderField.Visibility = Visibility.Visible;
        _dueTime.Visibility = Visibility.Collapsed;
        Show(new TaskFields(title, false, due, "normal", null, repeat), list);
        _delete.Visibility = Visibility.Collapsed;
        _notice.IsOpen = false;
        _notice.Severity = InfoBarSeverity.Informational;
        if (!IsOpen)
        {
            Visibility = Visibility.Visible;
            OpenChanged?.Invoke();
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _title.Focus());
    }

    /// <summary>
    /// After a change made outside the panel (a tick in the list or calendar, a sync, an undo): shows the stored task,
    /// leaving the title or notes alone while either has the keyboard focus (calendar-views R6d).
    /// </summary>
    public void Reload(string itemId, IReadOnlyDictionary<string, Collection> collections)
    {
        if (IsOpen && _itemId == itemId && Store?.GetItem(itemId) is { } item && collections.TryGetValue(item.CollectionId, out var list))
        {
            _all = collections;
            Show(item, list, keepText: _title.IsKeyboardFocused || _notes.IsKeyboardFocused);
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

    private void Show(Item item, Collection list, bool keepText = false)
    {
        // The backend is the account's type, not the shape of the stored data.
        var type = Store?.GetAccounts().FirstOrDefault(a => a.Id == list.AccountId)?.Type ?? AccountType.MS365;
        var fields = TaskQuery.Fields(item, TimeZoneInfo.Local, type);
        _reminderField.Visibility = Visibility.Visible;
        _recurring = item.IsRecurring;
        _unshownRepeat = item.IsRecurring && fields.Repeat is null;
        // A due time in a list that can't be written is shown for reading only; a writable one has the Due time field.
        var dueTime = type == AccountType.CalDAV && !_writable ? TaskQuery.DueTime(item, TimeZoneInfo.Local) : null;
        _dueTime.Text = dueTime is { } t ? $"Due at {t.ToString("t", System.Globalization.CultureInfo.CurrentCulture)}" : "";
        _dueTime.Visibility = dueTime is null ? Visibility.Collapsed : Visibility.Visible;
        Show(fields, list, keepText);
    }

    private void Show(TaskFields fields, Collection list, bool keepText = false)
    {
        _loading = true;
        if (!keepText)
        {
            _title.Text = fields.Title;
            _notes.Text = (fields.Notes ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        }
        _completed.IsChecked = fields.Completed;
        _due.SelectedDate = fields.Due?.ToDateTime(TimeOnly.MinValue);
        _timeField.Visibility = _calDav && _writable ? Visibility.Visible : Visibility.Collapsed;
        ShowDueTime(fields.DueTime);
        _importance.SelectedIndex = Array.IndexOf(Importances, fields.Importance);
        _reminderDate.SelectedDate = fields.Reminder?.Date;
        _reminderTime.Time = fields.Reminder?.TimeOfDay ?? TimeSpan.FromHours(9);
        ShowLists(list);

        var editable = _writable && !_completeOnly;
        _title.IsReadOnly = _notes.IsReadOnly = !editable;
        _due.IsEnabled = _importance.IsEnabled = _listBox.IsEnabled = editable;
        _reminderDate.IsEnabled = _reminderTime.IsEnabled = _clearReminder.IsEnabled = editable;
        // View-only and complete-only tasks show their repeat, disabled; a pattern the panel can't show gets a note.
        _repeat.Visibility = _unshownRepeat ? Visibility.Collapsed : Visibility.Visible;
        _repeatNote.Visibility = _unshownRepeat ? Visibility.Visible : Visibility.Collapsed;
        _repeat.Show(fields.Repeat ?? RepeatRule.None, fields.Due ?? Today);
        _repeat.SetEnabled(editable && !_calDav);
        UpdateClearDue();
        _completed.IsEnabled = _writable;
        _delete.Visibility = _writable ? Visibility.Visible : Visibility.Collapsed;
        _loading = false;
    }

    /// <summary>Clearing the due date is not offered while a repeat is set; it would restart the repeat from today.</summary>
    private void UpdateClearDue()
    {
        var editable = _writable && !_completeOnly;
        var repeating = _unshownRepeat || _calDav && _recurring || _repeat.Read() is { Frequency: not RepeatFrequency.None };
        _clearDue.IsEnabled = editable && !repeating;
        _clearDue.ToolTip = editable && repeating ? _calDav ? "A repeating task needs a due date" : "Set Doesn't repeat first" : null;
    }

    /// <summary>Shows the due time (null: none, with an Add time button) without saving.</summary>
    private void ShowDueTime(TimeOnly? time)
    {
        var was = _loading;
        _loading = true;
        if (time is { } t)
        {
            _dueTimeBox.Time = t.ToTimeSpan();
        }
        _dueTimeBox.Visibility = _clearTime.Visibility = time is null ? Visibility.Collapsed : Visibility.Visible;
        _addTime.Visibility = time is null ? Visibility.Visible : Visibility.Collapsed;
        _loading = was;
        UpdateTimeButtons();
    }

    private void UpdateTimeButtons()
    {
        var editable = _writable && !_completeOnly;
        _addTime.IsEnabled = editable && _due.SelectedDate is not null;
        _dueTimeBox.IsEnabled = _clearTime.IsEnabled = editable;
    }

    private bool IsCalDav(Collection list) =>
        Store?.GetAccounts().FirstOrDefault(a => a.Id == list.AccountId)?.Type == AccountType.CalDAV;

    /// <summary>The task's editor fields for its backend.</summary>
    private TaskFields Read(Item item) =>
        _calDav ? CalDavTasks.Read(item, TimeZoneInfo.Local) : GraphTaskMapper.Read(item);

    private TaskFields ReadFields(TaskFields old)
    {
        var title = _title.Text.Trim();
        return new TaskFields(
            title.Length > 0 ? title : old.Title,
            _completed.IsChecked == true,
            _due.SelectedDate is { } d ? DateOnly.FromDateTime(d.Date) : null,
            _importance.SelectedIndex >= 0 ? Importances[_importance.SelectedIndex] : old.Importance,
            _notes.Text.Replace("\r\n", "\n").Replace("\r", "\n"),
            _repeat.Read(),
            _reminderDate.SelectedDate is { } reminder ? reminder.Date + _reminderTime.Time : null,
            _calDav && _due.SelectedDate is not null && _dueTimeBox.Visibility == Visibility.Visible ? TimeOnly.FromTimeSpan(_dueTimeBox.Time) : null);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private DateOnly DueOrToday() => _due.SelectedDate is { } d ? DateOnly.FromDateTime(d.Date) : Today;

    /// <summary>
    /// A repeat sets or moves the due date (no due date: today; weekly: the first chosen weekday), so the pickers show
    /// what was saved.
    /// </summary>
    private void ShowSavedDue(TaskFields sent, Item saved)
    {
        var fields = Read(saved);
        if (fields.Due != sent.Due)
        {
            _loading = true;
            _due.SelectedDate = fields.Due?.ToDateTime(TimeOnly.MinValue);
            _repeat.SetStart(DueOrToday());
            _loading = false;
        }
    }

    private void Save()
    {
        if (!_loading && IsOpen && _newList is { } newList)
        {
            Create(newList);
            return;
        }
        if (_loading || !_writable || !IsOpen || _itemId is not { } id || Store is not { } store)
        {
            return;
        }
        if (store.GetItem(id) is not { } item)
        {
            // Deleted meanwhile (e.g. by a sync).
            Hide();
            return;
        }
        try
        {
            var old = Read(item);
            var fields = _completeOnly ? old with { Completed = _completed.IsChecked == true } : ReadFields(old);
            var edited = _calDav ? CalDavTasks.Edit(item, fields, TimeZoneInfo.Local, DateTimeOffset.UtcNow)
                : GraphTasks.Edit(item, fields, TimeZoneInfo.Local);
            if (edited is not null)
            {
                using (store.BeginUndo("Task edited"))
                {
                    store.UpdateItem(edited);
                }
                if (_calDav && _collection is { } list)
                {
                    // Completing a repeating task advances it (tasks R7), and a due move shifts a relative reminder: show what was saved.
                    Show(edited, list, keepText: true);
                }
                else
                {
                    ShowSavedDue(fields, edited);
                }
                Log.Information("Task {ItemId} saved", id);
                Changed?.Invoke();
            }
            // An empty title keeps the stored one; the box shows it again.
            if (_title.Text.Trim().Length == 0 && fields.Title.Length > 0)
            {
                _loading = true;
                _title.Text = fields.Title;
                _loading = false;
            }
            ClearSaveWarning();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving task {ItemId} failed", id);
            ShowSaveWarning();
        }
    }

    private void ShowSaveWarning()
    {
        _notice.Severity = InfoBarSeverity.Warning;
        _notice.Message = "Something went wrong; the task was not saved.";
        _notice.IsOpen = true;
    }

    private void ClearSaveWarning()
    {
        if (_notice.Severity == InfoBarSeverity.Warning)
        {
            _notice.IsOpen = false;
            _notice.Severity = InfoBarSeverity.Informational;
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
            var fields = ReadFields(new TaskFields("", false, null, "normal", null));
            var task = _calDav ? CalDavTasks.Create(list, fields, TimeZoneInfo.Local, DateTimeOffset.UtcNow)
                : GraphTasks.Create(list, fields, TimeZoneInfo.Local);
            using (store.BeginUndo("Task added"))
            {
                store.CreateItem(task);
            }
            ShowSavedDue(fields, task);
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
            ShowSaveWarning();
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
            using (store.BeginUndo("Task deleted"))
            {
                store.DeleteItem(id);
            }
            Log.Information("Task {ItemId} deleted", id);
            UndoOffered?.Invoke("Task deleted");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Deleting task {ItemId} failed", id);
        }
        Hide();
        Changed?.Invoke();
    }

}
