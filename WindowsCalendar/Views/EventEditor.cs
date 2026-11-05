using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Serilog;
using Windows.System;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Views;

/// <summary>
/// The event editor side panel (calendar-views R6, R6c, R6d; interim fields). It auto-saves: text fields when they
/// lose focus (or on Enter in the title), pickers and checkboxes at once. A new event is created once it has a
/// title. Recurring events and read-only calendars are view-only.
/// </summary>
internal sealed partial class EventEditor : UserControl
{
    public const double PanelWidth = 400;

    private readonly TextBlock _heading = new() { Style = Theme.Style("SubtitleTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _delete = IconButton("", "Delete");
    private readonly InfoBar _notice = new() { IsClosable = false, Severity = InfoBarSeverity.Informational };
    private readonly TextBox _title = new() { PlaceholderText = "Add a title", Header = "Title" };
    private readonly CheckBox _allDay = new() { Content = "All day" };
    private readonly CalendarDatePicker _startDate = new() { Header = "Start" };
    private readonly TimePicker _startTime = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly CalendarDatePicker _endDate = new() { Header = "End" };
    private readonly TimePicker _endTime = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBox _location = new() { Header = "Location" };
    private readonly ComboBox _calendar = new() { Header = "Calendar", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _description = new()
    {
        Header = "Description",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 120,
    };

    private string? _itemId;
    private bool _loading;
    private bool _readOnly;
    private DateTime _shownStart;

    public EventEditor()
    {
        var close = IconButton("", "Close (Esc)");
        close.Click += (_, _) => Close();
        _delete.Click += async (_, _) => await DeleteAsync();

        var header = new Grid { ColumnSpacing = 4 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_heading);
        Grid.SetColumn(_delete, 1);
        Grid.SetColumn(close, 2);
        header.Children.Add(_delete);
        header.Children.Add(close);

        var form = new StackPanel { Spacing = 12, Padding = new Thickness(16, 0, 16, 16) };
        form.Children.Add(_notice);
        form.Children.Add(_title);
        form.Children.Add(_allDay);
        form.Children.Add(DateTimeRow(_startDate, _startTime));
        form.Children.Add(DateTimeRow(_endDate, _endTime));
        form.Children.Add(_location);
        form.Children.Add(_calendar);
        form.Children.Add(_description);

        var root = new Grid { RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        header.Margin = new Thickness(16, 12, 8, 0);
        var scroll = new ScrollViewer { Content = form };
        Grid.SetRow(scroll, 1);
        root.Children.Add(header);
        root.Children.Add(scroll);
        Content = root;
        Width = PanelWidth;
        Visibility = Visibility.Collapsed;

        // Text saves when it loses focus; Enter in the title saves too (R6d).
        foreach (var box in new[] { _title, _location, _description })
        {
            box.LostFocus += (_, _) => Save();
        }
        _title.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                Save();
            }
        };
        // Pickers and checkboxes save at once.
        _allDay.Click += (_, _) => { ShowTimePickers(); Save(); };
        _startDate.DateChanged += (_, _) => StartChanged();
        _startTime.TimeChanged += (_, _) => StartChanged();
        _endDate.DateChanged += (_, _) => Save();
        _endTime.TimeChanged += (_, _) => Save();
        _calendar.SelectionChanged += (_, _) => Save();

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

    private static LocalStore? Store => ((App)Application.Current).Store;

    /// <summary>Opens an event; the event being edited is saved first (R6d: switching needs no confirmation).</summary>
    public void OpenExisting(string itemId, IReadOnlyDictionary<string, Collection> collections)
    {
        Save();
        if (Store?.GetItem(itemId) is not { } item || !collections.TryGetValue(item.CollectionId, out var collection))
        {
            return;
        }
        var account = Store.GetAccounts().FirstOrDefault(a => a.Id == collection.AccountId);
        _itemId = itemId;
        _readOnly = collection.ReadOnly || item.IsRecurring || account?.Type != AccountType.CalDAV;
        var fields = CalDavEvents.Read(item, TimeZoneInfo.Local);
        var choices = collections.Values
            .Where(c => c.Kind == CollectionKind.Calendar && c.AccountId == collection.AccountId && (!c.ReadOnly || c.Id == collection.Id))
            .ToList();
        Show("Event", fields, choices, collection.Id);
        _notice.Message = collection.SourceUrl is not null ? "This is a subscribed calendar and can't be edited here."
            : collection.ReadOnly ? "This calendar is read-only."
            : item.IsRecurring ? "Editing recurring events comes later. This event is view-only for now."
            : _readOnly ? "Editing events in this account comes later." : "";
        _notice.IsOpen = _notice.Message.Length > 0;
    }

    /// <summary>Opens a new event at a clicked slot (R5 interim); it is created once it has a title.</summary>
    public void OpenNew(DateTime start, bool allDay, IReadOnlyDictionary<string, Collection> collections)
    {
        Save();
        var accounts = Store?.GetAccounts().Where(a => a.Type == AccountType.CalDAV).Select(a => a.Id).ToHashSet() ?? [];
        var choices = collections.Values.Where(c => c.Kind == CollectionKind.Calendar && !c.ReadOnly && accounts.Contains(c.AccountId)).ToList();
        if (choices.Count == 0)
        {
            return;
        }
        var defaultId = Store?.DefaultCalendarId is { } id && choices.Any(c => c.Id == id) ? id : choices[0].Id;
        _itemId = null;
        _readOnly = false;
        var end = allDay ? start.Date.AddDays(1) : start.AddHours(1);
        Show("New event", new EventFields("", allDay, start, end, null, null), choices, defaultId);
        _notice.IsOpen = false;
        _title.Focus(FocusState.Programmatic);
    }

    /// <summary>Saves what is typed and closes; a new event without a title is discarded (R6d).</summary>
    public void Close()
    {
        Save();
        Hide();
    }

    /// <summary>Closes without saving, e.g. when the event was deleted.</summary>
    public void Hide()
    {
        _itemId = null;
        if (IsOpen)
        {
            Visibility = Visibility.Collapsed;
            OpenChanged?.Invoke();
        }
    }

    /// <summary>The event that is open, if it exists in the store.</summary>
    public string? ItemId => _itemId;

    private void Show(string heading, EventFields fields, List<Collection> choices, string selectedId)
    {
        _loading = true;
        _heading.Text = heading;
        _title.Text = fields.Title;
        _allDay.IsChecked = fields.AllDay;
        _startDate.Date = fields.Start.Date;
        _startTime.Time = fields.Start.TimeOfDay;
        // All-day end dates are shown inclusive.
        _endDate.Date = fields.AllDay ? fields.End.Date.AddDays(-1) : fields.End.Date;
        _endTime.Time = fields.End.TimeOfDay;
        _location.Text = fields.Location ?? "";
        _description.Text = (fields.Description ?? "").Replace("\r\n", "\r").Replace("\n", "\r");
        _shownStart = fields.Start;

        _calendar.Items.Clear();
        foreach (var c in choices)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            label.Children.Add(ShapeMarker.Create(c.Shape, Theme.Calendar(c.Colour).Swatch));
            label.Children.Add(new TextBlock { Text = c.Name });
            _calendar.Items.Add(new ComboBoxItem { Content = label, Tag = c });
        }
        _calendar.SelectedIndex = choices.FindIndex(c => c.Id == selectedId);

        _delete.Visibility = _itemId is null || _readOnly ? Visibility.Collapsed : Visibility.Visible;
        foreach (var box in new[] { _title, _location, _description })
        {
            box.IsReadOnly = _readOnly;
        }
        foreach (var control in new Control[] { _allDay, _startDate, _startTime, _endDate, _endTime, _calendar })
        {
            control.IsEnabled = !_readOnly;
        }
        ShowTimePickers();
        _loading = false;

        if (!IsOpen)
        {
            Visibility = Visibility.Visible;
            OpenChanged?.Invoke();
        }
    }

    private void ShowTimePickers() =>
        _startTime.Visibility = _endTime.Visibility = _allDay.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Moving the start keeps the duration: the end moves with it.</summary>
    private void StartChanged()
    {
        if (_loading)
        {
            return;
        }
        var fields = ReadFields();
        var shift = fields.Start - _shownStart;
        if (shift != TimeSpan.Zero)
        {
            var end = fields.End + shift;
            _loading = true;
            _endDate.Date = fields.AllDay ? end.Date.AddDays(-1) : end.Date;
            _endTime.Time = end.TimeOfDay;
            _loading = false;
        }
        Save();
    }

    /// <summary>The form as editor fields. An end not after the start is moved to one hour (or one day) later.</summary>
    private EventFields ReadFields()
    {
        var allDay = _allDay.IsChecked == true;
        var startDate = _startDate.Date?.Date ?? DateTime.Today;
        var endDate = _endDate.Date?.Date ?? startDate;
        var start = allDay ? startDate : startDate + _startTime.Time;
        var end = allDay ? endDate.AddDays(1) : endDate + _endTime.Time;
        if (end <= start)
        {
            end = allDay ? start.AddDays(1) : start.AddHours(1);
            var loading = _loading;
            _loading = true;
            _endDate.Date = allDay ? start.Date : end.Date;
            _endTime.Time = end.TimeOfDay;
            _loading = loading;
        }
        var description = _description.Text.Replace("\r\n", "\n").Replace("\r", "\n");
        return new EventFields(_title.Text.Trim(), allDay, start, end, _location.Text.Trim(), description);
    }

    private void Save()
    {
        if (_loading || _readOnly || !IsOpen || Store is not { } store || (_calendar.SelectedItem as ComboBoxItem)?.Tag is not Collection target)
        {
            return;
        }
        var fields = ReadFields();
        _shownStart = fields.Start;
        var now = DateTimeOffset.UtcNow;
        var zone = TimeZoneInfo.Local;
        try
        {
            if (_itemId is null)
            {
                if (fields.Title.Length == 0)
                {
                    return;
                }
                var created = CalDavEvents.Create(target, fields, zone, now);
                store.CreateItem(created);
                _itemId = created.Id;
                _heading.Text = "Event";
                _delete.Visibility = Visibility.Visible;
                Log.Information("Event {ItemId} created", created.Id);
            }
            else
            {
                if (store.GetItem(_itemId) is not { } item)
                {
                    // Deleted meanwhile (e.g. by a sync).
                    Hide();
                    return;
                }
                if (target.Id != item.CollectionId)
                {
                    _itemId = store.MoveItem(item.Id, CalDavEvents.MoveTo(item, target));
                    item = store.GetItem(_itemId)!;
                    Log.Information("Event {ItemId} moved to another calendar", _itemId);
                }
                if (CalDavEvents.Edit(item, fields, zone, now) is { } edited)
                {
                    store.UpdateItem(edited);
                }
            }
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving event {ItemId} failed", _itemId);
        }
    }

    /// <summary>Asks first until undo exists (R10 interim).</summary>
    private async System.Threading.Tasks.Task DeleteAsync()
    {
        if (_itemId is not { } id || Store is not { } store)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete this event?",
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
            Log.Information("Event {ItemId} deleted", id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Deleting event {ItemId} failed", id);
        }
        Hide();
        Changed?.Invoke();
    }

    private static Grid DateTimeRow(CalendarDatePicker date, TimePicker time)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        date.HorizontalAlignment = HorizontalAlignment.Stretch;
        time.MinWidth = 0;
        Grid.SetColumn(time, 1);
        row.Children.Add(date);
        row.Children.Add(time);
        return row;
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
