using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Serilog;
using Windows.System;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Views;

/// <summary>
/// The event editor side panel (calendar-views R6, R6c, R6d; interim fields). It auto-saves: text fields when they
/// lose focus (or on Enter in the title), pickers and checkboxes at once. A new event is created once it has a
/// title. Read-only calendars are view-only. A repeating event opens at the clicked occurrence; the first change asks
/// This event / Entire series, which holds until the panel closes or switches event (recurring-events interim).
/// The repeat rule (R3 interim) auto-saves on new and single events; on a series it is staged until Apply.
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
    private readonly ComboBox _showAs = new() { Header = "Show as", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _description = new()
    {
        Header = "Description",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 120,
    };
    private readonly RepeatPanel _repeat = new();

    private string? _itemId;
    // The rule as saved; null when the controls can't show it (R7).
    private RepeatRule? _savedRule;
    // Opened as a new or single event: rule changes save at once, with no scope question.
    private bool _ruleAutosave;
    // An MS365 event: Graph JSON (EventEditor.Graph.cs).
    private bool _graph;
    private DateTimeOffset? _recurrence;
    private EditScope? _scope;
    private bool _allowFollowing;
    private bool _asking;
    private bool _loading;
    private bool _readOnly;
    private DateTime _shownStart;
    // A new event whose show-as the user has not chosen: it follows all-day (event-show-as R4).
    private bool _defaultShowAs;

    private readonly DropDownButton _switch;
    private readonly MenuFlyoutItem _switchItem;

    public EventEditor()
    {
        _switch = ViewParts.KindSwitch("New event", "New task", () => SwitchRequested?.Invoke(_title.Text), out _switchItem);
        var close = IconButton("", "Close (Esc)");
        close.Click += (_, _) => Close();
        _delete.Click += async (_, _) => await DeleteAsync();

        var header = new Grid { ColumnSpacing = 4 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_heading);
        header.Children.Add(_switch);
        Grid.SetColumn(_delete, 1);
        Grid.SetColumn(close, 2);
        header.Children.Add(_delete);
        header.Children.Add(close);

        var form = new StackPanel { Spacing = 12, Padding = new Thickness(16, 0, 16, 16) };
        form.Children.Add(_notice);
        form.Children.Add(_join);
        form.Children.Add(_title);
        form.Children.Add(_allDay);
        form.Children.Add(DateTimeRow(_startDate, _startTime));
        form.Children.Add(DateTimeRow(_endDate, _endTime));
        form.Children.Add(_repeat);
        form.Children.Add(_location);
        form.Children.Add(_showAs);
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
        // A link typed into the location or description may make the event joinable (online-meeting-join R1).
        foreach (var box in new[] { _title, _location, _description })
        {
            box.LostFocus += (_, _) =>
            {
                Save();
                RefreshJoin();
            };
        }
        _join.Click += (_, _) => Join();
        _title.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                Save();
            }
        };
        // Pickers and checkboxes save at once.
        _allDay.Click += (_, _) =>
        {
            ShowTimePickers();
            if (_defaultShowAs)
            {
                ShowShowAs(_allDay.IsChecked == true ? ShowAs.Free : ShowAs.Busy);
            }
            Save();
        };
        _showAs.SelectionChanged += (_, _) =>
        {
            if (!_loading)
            {
                _defaultShowAs = false;
                Save();
            }
        };
        _startDate.DateChanged += (_, _) => StartChanged();
        _startTime.TimeChanged += (_, _) => StartChanged();
        _endDate.DateChanged += (_, _) => Save();
        _endTime.TimeChanged += (_, _) => Save();
        _calendar.SelectionChanged += (_, _) => CalendarChanged();
        _repeat.RuleChanged += RuleChanged;
        _repeat.ApplyClicked += async () => await ApplyRuleAsync();

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

    /// <summary>
    /// Raised after one occurrence, or an occurrence and those after it, was deleted: banner title, item id, payload
    /// before and after, for Undo (undo interim).
    /// </summary>
    public event Action<string, string, string, string>? OccurrenceDeleted;

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Raised with the typed title when a new event is switched to a new task from its heading.</summary>
    public event Action<string>? SwitchRequested;

    /// <summary>Whether a new event may switch to a new task (there is a To Do list to put it in).</summary>
    public bool CanSwitchToTask { set => _switchItem.IsEnabled = value; }

    private static LocalStore? Store => ((App)Application.Current).Store;

    /// <summary>
    /// Opens an event, at <paramref name="recurrence"/>'s occurrence if it repeats; the event being edited is saved first
    /// (R6d: switching needs no confirmation).
    /// </summary>
    public async void OpenExisting(string itemId, DateTimeOffset? recurrence, IReadOnlyDictionary<string, Collection> collections) =>
        await OpenExistingAsync(itemId, recurrence, collections);

    /// <summary>
    /// Opens the event and runs its Delete, with the same confirmation and repeating-event prompt (Del on a selected
    /// event, keyboard-navigation interim). Nothing happens if the event cannot be deleted here.
    /// </summary>
    public async void OpenAndDelete(string itemId, DateTimeOffset? recurrence, IReadOnlyDictionary<string, Collection> collections)
    {
        await OpenExistingAsync(itemId, recurrence, collections);
        if (IsOpen && _itemId == itemId && _delete.Visibility == Visibility.Visible)
        {
            await DeleteAsync();
        }
    }

    private async System.Threading.Tasks.Task OpenExistingAsync(string itemId, DateTimeOffset? recurrence, IReadOnlyDictionary<string, Collection> collections)
    {
        if (!await DiscardRuleAsync())
        {
            return;
        }
        Save();
        if (Store?.GetItem(itemId) is not { } item || !collections.TryGetValue(item.CollectionId, out var collection))
        {
            return;
        }
        var account = Store.GetAccounts().FirstOrDefault(a => a.Id == collection.AccountId);
        _all = collections;
        if (account?.Type == AccountType.MS365)
        {
            OpenGraph(item, collection, collections);
            return;
        }
        EventFields fields;
        try
        {
            fields = recurrence is { } r ? CalDavRecurrence.ReadOccurrence(item, r, TimeZoneInfo.Local) : CalDavEvents.Read(item, TimeZoneInfo.Local);
        }
        catch (EditRefusedException)
        {
            return;
        }
        _graph = false;
        _occurrenceId = null;
        _itemId = itemId;
        _recurrence = recurrence;
        _scope = null;
        _allowFollowing = recurrence is not null && !CalDavRecurrence.IsOrganisedMeeting(item, account?.Email);
        _readOnly = collection.ReadOnly || account?.Type != AccountType.CalDAV;
        _savedRule = CalDavRecurrence.ReadRule(item, TimeZoneInfo.Local);
        _ruleAutosave = recurrence is null;
        // Meetings and invitations stay in their calendar (calendar-views R8).
        var meeting = Core.Sync.EventTransfer.IsMeeting(item);
        Show("Event", fields, CalendarChoices(collection, !meeting), collection.Id);
        _calendar.IsEnabled = !_readOnly && !meeting;
        var message = collection.SourceUrl is not null ? "This is a subscribed calendar and can't be edited here."
            : collection.ReadOnly ? "This calendar is read-only."
            : _readOnly ? "Editing events in this account comes later."
            : _savedRule is null ? $"{CalDavRecurrence.Describe(item) ?? "Repeats"}. This repeat pattern can't be changed here." : "";
        ShowNotice(message, InfoBarSeverity.Informational);
    }

    /// <summary>Opens a new event at a clicked slot (R5 interim); it is created once it has a title.</summary>
    public async void OpenNew(DateTime start, bool allDay, IReadOnlyDictionary<string, Collection> collections, string title = "")
    {
        if (!await DiscardRuleAsync())
        {
            return;
        }
        Save();
        var accounts = Store?.GetAccounts().Select(a => a.Id).ToHashSet() ?? [];
        var choices = collections.Values.Where(c => c.Kind == CollectionKind.Calendar && !c.ReadOnly && accounts.Contains(c.AccountId)).ToList();
        if (choices.Count == 0)
        {
            return;
        }
        var defaultId = Store?.DefaultCalendarId is { } id && choices.Any(c => c.Id == id) ? id : choices[0].Id;
        _all = collections;
        _itemId = null;
        _recurrence = null;
        _scope = null;
        _readOnly = false;
        _graph = false;
        _occurrenceId = null;
        _savedRule = RepeatRule.None;
        _ruleAutosave = true;
        var end = allDay ? start.Date.AddDays(1) : start.AddHours(1);
        Show("New event", new EventFields(title, allDay, start, end, null, null, allDay ? ShowAs.Free : ShowAs.Busy), choices, defaultId);
        _notice.IsOpen = false;
        _title.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Saves what is typed and closes; a new event without a title is discarded (R6d). Unapplied rule changes are
    /// discarded after asking.
    /// </summary>
    public async void Close()
    {
        if (!await DiscardRuleAsync())
        {
            return;
        }
        Save();
        Hide();
    }

    /// <summary>A new event shows the New event / New task drop-down in place of its heading.</summary>
    private void ShowSwitch(bool isNew)
    {
        _switch.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
        _heading.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
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

    /// <summary>
    /// After a drag or undo changed the open event's times: shows them, leaving text being typed alone. A series move
    /// changes the occurrence's id from <paramref name="from"/> to <paramref name="to"/>; if the occurrence is gone the
    /// panel closes.
    /// </summary>
    public void Reload(string itemId, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        if (!IsOpen || _itemId is null || (_itemId != itemId && _occurrenceId != itemId) || Store?.GetItem(_itemId) is not { } item)
        {
            return;
        }
        if (from is not null && _recurrence == from)
        {
            _recurrence = to;
        }
        EventFields fields;
        try
        {
            fields = Fields(item);
        }
        catch (EditRefusedException)
        {
            Hide();
            return;
        }
        _loading = true;
        _allDay.IsChecked = fields.AllDay;
        _startDate.Date = fields.Start.Date;
        _startTime.Time = fields.Start.TimeOfDay;
        _endDate.Date = fields.AllDay ? fields.End.Date.AddDays(-1) : fields.End.Date;
        _endTime.Time = fields.End.TimeOfDay;
        _shownStart = fields.Start;
        ShowTimePickers();
        _loading = false;
        RefreshRule();
    }

    /// <summary>The event that is open, if it exists in the store.</summary>
    public string? ItemId => _itemId;

    /// <summary>The open event's fields: its occurrence's when it repeats.</summary>
    private EventFields Fields(Item item) => _graph ? GraphFields(item)
        : _recurrence is { } r ? CalDavRecurrence.ReadOccurrence(item, r, TimeZoneInfo.Local)
        : CalDavEvents.Read(item, TimeZoneInfo.Local);

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        _notice.Message = message;
        _notice.Severity = severity;
        _notice.IsOpen = message.Length > 0;
    }

    private void Show(string heading, EventFields fields, List<Collection> choices, string selectedId)
    {
        _loading = true;
        _heading.Text = heading;
        ShowSwitch(_itemId is null);
        ShowFields(fields);

        _calendar.Items.Clear();
        var accounts = Store?.GetAccounts() ?? [];
        foreach (var c in choices)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            label.Children.Add(ShapeMarker.Create(c.Shape, Theme.Calendar(c.Colour).Swatch));
            label.Children.Add(new TextBlock { Text = c.Name });
            // Its source, muted (calendar-views interim).
            if (accounts.FirstOrDefault(a => a.Id == c.AccountId) is { } account)
            {
                label.Children.Add(new TextBlock { Text = CollectionSource.For(account), Foreground = Theme.Brush("TextSecondaryBrush") });
            }
            _calendar.Items.Add(new ComboBoxItem { Content = label, Tag = c });
        }
        _calendar.SelectedIndex = choices.FindIndex(c => c.Id == selectedId);
        _defaultShowAs = _itemId is null;
        ShowShowAs(fields.ShowAs);

        _delete.Visibility = _itemId is null || _readOnly ? Visibility.Collapsed : Visibility.Visible;
        foreach (var box in new[] { _title, _location, _description })
        {
            box.IsReadOnly = _readOnly;
        }
        foreach (var control in new Control[] { _allDay, _startDate, _startTime, _endDate, _endTime, _showAs, _calendar })
        {
            control.IsEnabled = !_readOnly;
        }
        _repeat.Visibility = _savedRule is null ? Visibility.Collapsed : Visibility.Visible;
        _repeat.Show(_savedRule ?? RepeatRule.None, DateOnly.FromDateTime(fields.Start));
        _repeat.SetEnabled(!_readOnly);
        ShowTimePickers();
        RefreshJoin();
        _loading = false;

        if (!IsOpen)
        {
            Visibility = Visibility.Visible;
            OpenChanged?.Invoke();
        }
    }

    private void ShowFields(EventFields fields)
    {
        var loading = _loading;
        _loading = true;
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
        ShowShowAs(fields.ShowAs);
        ShowTimePickers();
        _loading = loading;
    }

    /// <summary>
    /// The show-as choices for the event's backend (event-show-as R2): all five for MS365, Busy and Free for CalDAV. A
    /// CalDAV event carrying another value (R3) shows it as a further choice. A new event moved to a calendar that
    /// lacks its value becomes Busy.
    /// </summary>
    private void ShowShowAs(ShowAs current)
    {
        var graph = _itemId is null ? (_calendar.SelectedItem as ComboBoxItem)?.Tag is Collection c && IsGraphCalendar(c) : _graph;
        List<ShowAs> values = graph ? [.. Enum.GetValues<ShowAs>()] : [ShowAs.Busy, ShowAs.Free];
        if (!values.Contains(current))
        {
            if (_itemId is null)
            {
                current = ShowAs.Busy;
            }
            else
            {
                values.Add(current);
            }
        }
        var loading = _loading;
        _loading = true;
        _showAs.Items.Clear();
        foreach (var value in values)
        {
            _showAs.Items.Add(new ComboBoxItem { Content = ShowAsValues.Label(value), Tag = value });
        }
        _showAs.SelectedIndex = values.IndexOf(current);
        _loading = loading;
    }

    private ShowAs ReadShowAs() => (_showAs.SelectedItem as ComboBoxItem)?.Tag is ShowAs value ? value : ShowAs.Busy;

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
        _repeat.SetStart(DateOnly.FromDateTime(fields.Start));
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
        return new EventFields(_title.Text.Trim(), allDay, start, end, _location.Text.Trim(), description, ReadShowAs());
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
                DateTimeOffset? recurrence = null;
                if (IsGraphCalendar(target))
                {
                    recurrence = CreateGraph(store, target, fields);
                }
                else
                {
                    var created = CalDavEvents.Create(target, fields, zone, now);
                    // A repeat chosen before the title is written with the new event (R3 interim).
                    if (CalDavRecurrence.ChangeRule(created, null, false, _repeat.Read(), target, zone, now) is { } repeating)
                    {
                        created = repeating.Original;
                        recurrence = repeating.Recurrence;
                    }
                    store.CreateItem(created);
                    _itemId = created.Id;
                    Log.Information("Event {ItemId} created", created.Id);
                }
                _recurrence = recurrence;
                _scope = recurrence is null ? null : EditScope.Series;
                _heading.Text = "Event";
                ShowSwitch(false);
                _delete.Visibility = Visibility.Visible;
                if (recurrence is not null)
                {
                    // A weekly start may have moved to the first chosen weekday.
                    Changed?.Invoke();
                    Reload(_itemId!);
                    return;
                }
            }
            else
            {
                if (store.GetItem(_itemId) is not { } item)
                {
                    // Deleted meanwhile (e.g. by a sync).
                    Hide();
                    return;
                }
                if (_graph)
                {
                    SaveGraph(store, item, fields, target);
                    return;
                }
                if (target.Id != item.CollectionId)
                {
                    _itemId = store.MoveItem(item.Id, CalDavEvents.MoveTo(item, target));
                    item = store.GetItem(_itemId)!;
                    Log.Information("Event {ItemId} moved to another calendar", _itemId);
                }
                if (_recurrence is { } recurrence)
                {
                    SaveRecurring(item, recurrence, fields, target);
                    return;
                }
                if (CalDavEvents.Edit(item, fields, zone, now) is { } edited)
                {
                    store.UpdateItem(edited);
                }
            }
            Changed?.Invoke();
            RefreshRule();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving event {ItemId} failed", _itemId);
        }
    }

    /// <summary>
    /// A change to a repeating event: asks the scope at the first change (R6d), then writes this occurrence, this and
    /// the following ones (a split; the panel then follows the new series, where the scope is the whole series), or
    /// the whole series. Cancel or a refused change puts the form back.
    /// </summary>
    private async void SaveRecurring(Item item, DateTimeOffset recurrence, EventFields fields, Collection collection)
    {
        var zone = TimeZoneInfo.Local;
        try
        {
            var current = CalDavRecurrence.ReadOccurrence(item, recurrence, zone);
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
                var scope = await ScopeDialog.AskAsync(XamlRoot, "Change a repeating event", _allowFollowing);
                _asking = false;
                if (_itemId != item.Id || _recurrence != recurrence)
                {
                    return;
                }
                if (scope is null)
                {
                    ShowFields(current);
                    return;
                }
                _scope = scope;
                // The form may have changed while the question was open.
                fields = ReadFields();
                item = Store?.GetItem(item.Id) ?? item;
            }

            Item? edited;
            var to = recurrence;
            if (_scope == EditScope.Following)
            {
                if (CalDavRecurrence.SplitSeries(item, recurrence, fields, collection, zone, DateTimeOffset.UtcNow) is not { } split)
                {
                    return;
                }
                if (split.Following is { } following)
                {
                    Store!.SplitItem(split.Original, following);
                    _itemId = following.Id;
                }
                else
                {
                    Store!.UpdateItem(split.Original);
                }
                _recurrence = split.Recurrence;
                _scope = EditScope.Series;
                _shownStart = fields.Start;
                Log.Information("Repeating event {ItemId} split", item.Id);
                Changed?.Invoke();
                RefreshRule();
                return;
            }
            if (_scope == EditScope.Occurrence)
            {
                edited = CalDavRecurrence.EditOccurrence(item, recurrence, fields, zone, DateTimeOffset.UtcNow);
            }
            else
            {
                to = CalDavRecurrence.MovedRecurrence(item, recurrence, fields, zone);
                edited = CalDavRecurrence.EditSeries(item, recurrence, fields, zone, DateTimeOffset.UtcNow);
            }
            if (edited is null)
            {
                return;
            }
            Store!.UpdateItem(edited);
            _recurrence = to;
            _shownStart = fields.Start;
            Log.Information("Repeating event {ItemId} changed ({Scope})", item.Id, _scope);
            Changed?.Invoke();
            RefreshRule();
        }
        catch (EditRefusedException ex)
        {
            ShowNotice(ex.Message, InfoBarSeverity.Warning);
            if (Store?.GetItem(item.Id) is { } latest)
            {
                ShowFields(Fields(latest));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving repeating event {ItemId} failed", item.Id);
        }
    }

    // ---- Repeat rule (recurring-events R3 interim) ----

    /// <summary>A series' rule differs from the saved one and has not been applied.</summary>
    private bool RuleDirty => !_ruleAutosave && _savedRule is not null && !_repeat.Read().Equals(_savedRule);

    private void RuleChanged()
    {
        if (_loading || _readOnly || !IsOpen)
        {
            return;
        }
        if (_ruleAutosave)
        {
            SaveRule();
            return;
        }
        _repeat.ApplyVisible = RuleDirty;
    }

    /// <summary>Shows the saved rule again after a save moved the series (e.g. its weekday), unless one is staged.</summary>
    private void RefreshRule()
    {
        if (_itemId is null || RuleDirty || Store?.GetItem(_itemId) is not { } item)
        {
            return;
        }
        try
        {
            _savedRule = _graph ? GraphRecurrence.ReadRule(item, TimeZoneInfo.Local) : CalDavRecurrence.ReadRule(item, TimeZoneInfo.Local);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading the repeat of event {ItemId} failed", _itemId);
            return;
        }
        _repeat.Visibility = _savedRule is null ? Visibility.Collapsed : Visibility.Visible;
        _repeat.Show(_savedRule ?? RepeatRule.None, DateOnly.FromDateTime(_startDate.Date?.Date ?? DateTime.Today));
    }

    /// <summary>A new or single event: the rule saves at once. A new event without a title gets it when created.</summary>
    private void SaveRule()
    {
        if (_itemId is null || Store is not { } store || store.GetItem(_itemId) is not { } item
            || (_calendar.SelectedItem as ComboBoxItem)?.Tag is not Collection target)
        {
            return;
        }
        try
        {
            if (_graph)
            {
                SaveGraphRule(store, item, target);
            }
            else if (CalDavRecurrence.ChangeRule(item, _recurrence, false, _repeat.Read(), target, TimeZoneInfo.Local, DateTimeOffset.UtcNow) is { } change)
            {
                store.UpdateItem(change.Original);
                RuleSaved(change);
            }
        }
        catch (EditRefusedException ex)
        {
            ShowNotice(ex.Message, InfoBarSeverity.Warning);
            RefreshRule();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Changing the repeat of event {ItemId} failed", _itemId);
        }
    }

    /// <summary>
    /// Apply on a series: asks This and following / All (only All for meetings you organise), confirms dropping changed
    /// occurrences, then saves once. A split is one linked outbox group, and the panel follows the new series.
    /// </summary>
    private async System.Threading.Tasks.Task ApplyRuleAsync()
    {
        if (_asking || _itemId is not { } id || _recurrence is not { } recurrence || Store is not { } store
            || (_calendar.SelectedItem as ComboBoxItem)?.Tag is not Collection target)
        {
            return;
        }
        var rule = _repeat.Read();
        _asking = true;
        try
        {
            if (_graph)
            {
                if (store.GetItem(id) is { } master)
                {
                    await ApplyGraphRuleAsync(store, master, recurrence, target);
                }
                return;
            }
            var scope = _allowFollowing
                ? await ScopeDialog.AskAsync(XamlRoot, "Change the repeat pattern", true, allowOccurrence: false)
                : EditScope.Series;
            if (scope is null || _itemId != id || store.GetItem(id) is not { } item)
            {
                return;
            }
            var change = CalDavRecurrence.ChangeRule(item, recurrence, scope == EditScope.Following, rule, target, TimeZoneInfo.Local,
                DateTimeOffset.UtcNow);
            if (change is null)
            {
                _repeat.ApplyVisible = false;
                return;
            }
            if (change.Lost > 0)
            {
                var confirm = Destructive.Apply(new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Change the repeat pattern?",
                    Content = change.Lost == 1 ? "1 changed occurrence will be lost." : $"{change.Lost} changed occurrences will be lost.",
                    PrimaryButtonText = "Change",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                });
                if (await confirm.ShowAsync() != ContentDialogResult.Primary || _itemId != id)
                {
                    return;
                }
            }
            if (change.Following is { } following)
            {
                store.SplitItem(change.Original, following);
                _itemId = following.Id;
                _scope = EditScope.Series;
            }
            else
            {
                store.UpdateItem(change.Original);
            }
            RuleSaved(change);
        }
        catch (EditRefusedException ex)
        {
            ShowNotice(ex.Message, InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Changing the repeat of event {ItemId} failed", id);
        }
        finally
        {
            _asking = false;
        }
    }

    /// <summary>After a rule change: the panel follows the occurrence (or the single event) and shows the saved rule.</summary>
    private void RuleSaved(RuleChange change)
    {
        _recurrence = change.Recurrence;
        if (_recurrence is null)
        {
            // No longer repeating: later rule changes save at once again.
            _scope = null;
            _ruleAutosave = true;
        }
        else if (_ruleAutosave)
        {
            _scope = EditScope.Series;
        }
        _savedRule = null;
        Log.Information("Repeat of event {ItemId} changed", _itemId);
        Changed?.Invoke();
        Reload(_itemId!);
    }

    /// <summary>Before closing or switching event: true to go on. Unapplied rule changes are discarded after asking.</summary>
    private async System.Threading.Tasks.Task<bool> DiscardRuleAsync()
    {
        if (!IsOpen || _readOnly || !RuleDirty)
        {
            return true;
        }
        if (_asking)
        {
            return false;
        }
        _asking = true;
        try
        {
            var dialog = Destructive.Apply(new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Discard repeat changes?",
                Content = "The repeat pattern was changed but not applied.",
                PrimaryButtonText = "Discard",
                CloseButtonText = "Keep editing",
                DefaultButton = ContentDialogButton.Close,
            });
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return false;
            }
        }
        finally
        {
            _asking = false;
        }
        _repeat.ApplyVisible = false;
        return true;
    }

    /// <summary>
    /// Asks first until undo exists (R10 interim). A repeating event asks the scope: one occurrence, or it and the
    /// following ones, go at once with Undo; the entire series (or "following" from the first) asks for confirmation.
    /// </summary>
    private async System.Threading.Tasks.Task DeleteAsync()
    {
        if (_itemId is not { } id || Store is not { } store)
        {
            return;
        }
        if (_recurrence is { } recurrence)
        {
            var scope = await ScopeDialog.AskAsync(XamlRoot, "Delete a repeating event", _allowFollowing, delete: true);
            if (scope is null)
            {
                return;
            }
            if (_graph)
            {
                if (scope != EditScope.Series && DeleteGraphOccurrences(store, id, recurrence, scope.Value))
                {
                    Hide();
                    Changed?.Invoke();
                    return;
                }
                scope = EditScope.Series;
            }
            var zone = TimeZoneInfo.Local;
            if (scope == EditScope.Following && !_graph && store.GetItem(id) is { } series
                && CalDavRecurrence.DeleteFollowing(series, recurrence, zone, DateTimeOffset.UtcNow) is null)
            {
                // From the first occurrence, "this and following" is the whole series.
                scope = EditScope.Series;
            }
            if (scope != EditScope.Series && !_graph)
            {
                try
                {
                    if (store.GetItem(id) is { } item)
                    {
                        var deleted = scope == EditScope.Following
                            ? CalDavRecurrence.DeleteFollowing(item, recurrence, zone, DateTimeOffset.UtcNow)!
                            : CalDavRecurrence.DeleteOccurrence(item, recurrence, zone, DateTimeOffset.UtcNow);
                        store.UpdateItem(deleted);
                        Log.Information("Occurrence(s) of event {ItemId} deleted ({Scope})", id, scope);
                        OccurrenceDeleted?.Invoke(scope == EditScope.Following ? "Following events deleted" : "Occurrence deleted",
                            id, item.RawPayload, deleted.RawPayload);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Deleting occurrence of event {ItemId} failed", id);
                }
                Hide();
                Changed?.Invoke();
                return;
            }
        }
        var dialog = Destructive.Apply(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _recurrence is null ? "Delete this event?" : "Delete the entire series?",
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
