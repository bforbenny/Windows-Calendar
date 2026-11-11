using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Serilog;
using WindowsCalendar.Core.Reminders;
using WindowsCalendar.Core.Store;
using InfoBar = Wpf.Ui.Controls.InfoBar;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace WindowsCalendar.Views;

/// <summary>
/// The Settings page (settings-page interim): a section list on the left, the section's settings on the right.
/// First slice: Working hours, Appearance (week numbers) and Calendars (reminders only). Changes apply at once and are
/// stored locally.
/// </summary>
internal sealed class SettingsPage : UserControl
{
    private const string WeekNumbersFlag = "week-numbers";

    private readonly ListBox _sections = new() { Width = 220, SelectionMode = SelectionMode.Single, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };
    private readonly ContentControl _content = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Top,
    };
    private readonly InfoBar _hoursError = new()
    {
        Severity = InfoBarSeverity.Error,
        IsClosable = false,
        Message = "The end time must be after the start time. The change was not saved.",
    };
    private readonly DayOfWeek _weekStart;
    private bool _loading;

    public SettingsPage(DayOfWeek weekStart)
    {
        _weekStart = weekStart;
        var back = ViewParts.IconButton("", "Back");
        back.ToolTip = "Back (Esc)";
        back.VerticalAlignment = VerticalAlignment.Center;
        back.Click += (_, _) => BackRequested?.Invoke();

        var header = new SpacedStack { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12, 8, 12, 8) };
        header.Children.Add(back);
        var title = new TextBlock { Text = "Settings", Style = Theme.Style("SubtitleTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        header.Children.Add(title);

        _sections.Items.Add("Working hours");
        _sections.Items.Add("Appearance");
        _sections.Items.Add("Calendars");
        _sections.Items.Add("Calendar sets");
        _sections.Items.Add("About");
        _sections.SelectionChanged += (_, _) => ShowSection();

        var body = new Grid { Margin = new Thickness(12, 0, 24, 24) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var scroll = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(24, 0, 0, 0) };
        Grid.SetColumn(scroll, 1);
        body.Children.Add(_sections);
        body.Children.Add(scroll);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        root.Children.Add(header);
        root.Children.Add(body);
        Content = root;
    }

    /// <summary>Raised after a setting is saved, so the views redraw.</summary>
    public event Action? Changed;

    /// <summary>Raised after a calendar set is renamed, moved or deleted, so the sidebar selector and ticks refresh.</summary>
    public event Action? SetsChanged;

    /// <summary>Raised by Back; Esc is handled by the window.</summary>
    public event Action? BackRequested;

    private static LocalStore? Store => ((App)Application.Current).Store;

    /// <summary>Shows the page on <paramref name="section"/> (default the first), read fresh from the store.</summary>
    public void Open(string? section = null)
    {
        var index = Math.Max(0, _sections.Items.IndexOf(section ?? ""));
        if (_sections.SelectedIndex == index)
        {
            ShowSection();
        }
        else
        {
            _sections.SelectedIndex = index;
        }
    }

    private void ShowSection() =>
        _content.Content = _sections.SelectedIndex switch
        {
            1 => Appearance(),
            2 => CalendarsSection(),
            3 => CalendarSetsSection(),
            4 => AboutSection(),
            _ => WorkingHoursSection(),
        };

    // ---- About (settings-page R2, R7) ----

    /// <summary>The package version, Open log folder, and what uninstalling deletes.</summary>
    private static SpacedStack AboutSection()
    {
        var panel = Section("About", null);
        var v = global::Windows.ApplicationModel.Package.Current.Id.Version;
        var version = new TextBlock { Text = $"WindowsCalendar\nVersion {v.Major}.{v.Minor}.{v.Build}.{v.Revision}" };
        version.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(version);

        var logs = new Button { Content = "Open log folder", HorizontalAlignment = HorizontalAlignment.Left };
        logs.Click += (_, _) =>
        {
            var folder = System.IO.Path.Combine(global::Windows.Storage.ApplicationData.Current.LocalFolder.Path, "Logs");
            System.IO.Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        };
        panel.Children.Add(logs);

        panel.Children.Add(new TextBlock
        {
            Text = "Uninstalling deletes unsynced changes, conflict copies, colours, shapes, calendar sets and settings; "
                + "only what is on the servers is kept. Reminders keep firing for up to 48 hours after Quit.",
            Foreground = Theme.Brush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    // ---- Calendar sets (calendar-sets R2, R3; settings-page R2, R4) ----

    /// <summary>
    /// The user's sets in selector order: rename, move up / down, delete (asks first). Members are changed from the
    /// sidebar only. <paramref name="focus"/> is the button to focus after a move rebuilt the list.
    /// </summary>
    private SpacedStack CalendarSetsSection((string SetId, int Delta)? focus = null)
    {
        var panel = Section("Calendar sets",
            "\"All\" is built in and always first. To change a set's calendars, select it in the sidebar, tick the "
            + "calendars, then choose ⋯ > Update.");
        var sets = Store?.GetCalendarSets() ?? [];
        if (sets.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No sets yet. Tick calendars in the sidebar, then ⋯ > Save as new set.",
                Foreground = Theme.Brush("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        for (var i = 0; i < sets.Count; i++)
        {
            panel.Children.Add(CalendarSetRow(sets[i], i > 0, i < sets.Count - 1, focus));
        }
        return panel;
    }

    private Grid CalendarSetRow(CalendarSet set, bool canMoveUp, bool canMoveDown, (string SetId, int Delta)? focus)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new Wpf.Ui.Controls.TextBox { Text = set.Name, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(name, $"Name of the set {set.Name}");
        var current = set.Name;
        void Rename()
        {
            // An empty name is ignored: the old one comes back.
            if (string.IsNullOrWhiteSpace(name.Text) || Store is not { } store)
            {
                name.Text = current;
                return;
            }
            if (name.Text.Trim() == current)
            {
                return;
            }
            store.RenameCalendarSet(set.Id, name.Text);
            current = name.Text.Trim();
            name.Text = current;
            System.Windows.Automation.AutomationProperties.SetName(name, $"Name of the set {current}");
            Log.Information("Calendar set {SetId} renamed", set.Id);
            SetsChanged?.Invoke();
        }
        name.LostFocus += (_, _) => Rename();
        name.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                Rename();
                e.Handled = true;
            }
        };
        row.Children.Add(name);

        var buttons = new SpacedStack { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var (glyph, label, delta, enabled) in new[] { ("", "Move up", -1, canMoveUp), ("", "Move down", 1, canMoveDown) })
        {
            var move = ViewParts.IconButton(glyph, $"{label}: {set.Name}");
            move.ToolTip = label;
            move.IsEnabled = enabled;
            move.Click += (_, _) =>
            {
                Store?.MoveCalendarSet(set.Id, delta);
                Log.Information("Calendar set {SetId} moved", set.Id);
                _content.Content = CalendarSetsSection((set.Id, delta));
                SetsChanged?.Invoke();
            };
            // After a move rebuilt the list, the same button keeps the focus, or the other one once at an end.
            if (focus is { } f && f.SetId == set.Id && (enabled ? f.Delta == delta : f.Delta != delta))
            {
                move.Loaded += (_, _) => move.Focus();
            }
            buttons.Children.Add(move);
        }
        var delete = ViewParts.IconButton("", $"Delete the set {set.Name}");
        delete.ToolTip = "Delete";
        delete.Click += async (_, _) => await DeleteSetAsync(set.Id, current);
        buttons.Children.Add(delete);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);
        return row;
    }

    /// <summary>Destructive: asks first (settings-page R4); deleting the active set switches to "All" (calendar-sets R3).</summary>
    private async System.Threading.Tasks.Task DeleteSetAsync(string setId, string name)
    {
        var confirm = Destructive.Apply(new Wpf.Ui.Controls.ContentDialog(ViewParts.DialogHost)
        {
            Title = $"Delete the set '{name}'?",
            Content = "Calendars are not deleted.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = Wpf.Ui.Controls.ContentDialogButton.Close,
        });
        if (await confirm.ShowAsync() != Wpf.Ui.Controls.ContentDialogResult.Primary || Store is not { } store)
        {
            return;
        }
        store.DeleteCalendarSet(setId);
        Log.Information("Calendar set {SetId} deleted", setId);
        _content.Content = CalendarSetsSection();
        SetsChanged?.Invoke();
    }

    // ---- Calendars (reminders-notifications R5; settings-page interim) ----

    /// <summary>
    /// Per calendar and task list, under its account: "Reminders on", and for calendars events can be added to, the
    /// reminder new timed events get. A change reschedules the reminders at once.
    /// </summary>
    private SpacedStack CalendarsSection()
    {
        var panel = Section("Calendars", "Reminders show as Windows notifications. New all-day events get no reminder.");
        if (Store is not { } store)
        {
            return panel;
        }
        foreach (var account in store.GetAccounts())
        {
            var collections = store.GetCollections(account.Id);
            if (collections.Count == 0)
            {
                continue;
            }
            var heading = new TextBlock { Text = account.DisplayName, Style = Theme.Style("BodyStrongTextBlockStyle"), Margin = new Thickness(0, 8, 0, 0) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            panel.Children.Add(heading);
            foreach (var collection in collections)
            {
                panel.Children.Add(CalendarRow(store, collection));
            }
        }
        return panel;
    }

    private Grid CalendarRow(LocalStore store, Collection collection)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });

        var name = new SpacedStack { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(ShapeMarker.Create(collection.Shape, Theme.Calendar(collection.Colour).Swatch));
        var label = new TextBlock { Text = collection.Kind == CollectionKind.TaskList ? $"{collection.Name} (tasks)" : collection.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        name.Children.Add(label);
        row.Children.Add(name);

        if (collection.Kind == CollectionKind.Calendar && !collection.ReadOnly && collection.SourceUrl is null)
        {
            var defaults = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(8, 0, 8, 0) };
            System.Windows.Automation.AutomationProperties.SetName(defaults, $"Default reminder for {collection.Name}");
            var current = store.GetDefaultReminder(collection.Id);
            foreach (var choice in ReminderChoices.Timed)
            {
                defaults.Items.Add(new ComboBoxItem { Content = ReminderChoices.Label(choice, allDay: false), Tag = choice.MinutesBefore });
            }
            defaults.SelectedIndex = Math.Max(0, ReminderChoices.Timed.ToList().FindIndex(c => c.MinutesBefore == current));
            defaults.ToolTip = "Reminder for new timed events";
            defaults.SelectionChanged += (_, _) =>
            {
                var minutes = (defaults.SelectedItem as ComboBoxItem)?.Tag as int?;
                store.SetDefaultReminder(collection.Id, minutes);
                Log.Information("Default reminder for collection {CollectionId}: {Minutes}", collection.Id, minutes?.ToString(CultureInfo.InvariantCulture) ?? "none");
            };
            Grid.SetColumn(defaults, 1);
            row.Children.Add(defaults);
        }

        var on = new Wpf.Ui.Controls.ToggleSwitch { Content = "Reminders", IsChecked = collection.RemindersOn, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(on, $"Reminders for {collection.Name}");
        on.Click += (_, _) =>
        {
            var value = on.IsChecked == true;
            store.SetCollectionRemindersOn(collection.Id, value);
            Log.Information("Reminders {State} for collection {CollectionId}", value ? "on" : "off", collection.Id);
            _ = System.Threading.Tasks.Task.Run(() => ReminderToasts.Reschedule(store, ReminderPlanner.RunningWindow));
            Changed?.Invoke();
        };
        Grid.SetColumn(on, 2);
        row.Children.Add(on);
        return row;
    }

    // ---- Working hours (calendar-shading R1) ----

    private SpacedStack WorkingHoursSection()
    {
        _loading = true;
        var hours = Store?.GetWorkingHours() ?? WorkingHours.Default;
        var panel = Section("Working hours", "Time outside working hours and days off are shaded in the calendar.");

        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        var days = new SpacedStack { Orientation = Orientation.Horizontal, Spacing = 4 };
        var checks = new List<(DayOfWeek Day, CheckBox Box)>();
        for (var i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)_weekStart + i) % 7);
            var box = new CheckBox { Content = names[(int)day], IsChecked = hours.Days.Contains(day), MinWidth = 72 };
            checks.Add((day, box));
            days.Children.Add(box);
        }
        panel.Children.Add(ViewParts.Field("Working days", days));

        var start = new TimeBox { Time = hours.Start.ToTimeSpan() };
        var end = new TimeBox { Time = hours.End.ToTimeSpan() };
        var times = new SpacedStack { Orientation = Orientation.Horizontal, Spacing = 16 };
        times.Children.Add(ViewParts.Field("Start", start));
        times.Children.Add(ViewParts.Field("End", end));
        panel.Children.Add(times);
        _hoursError.IsOpen = false;
        // The banner is reused: take it off the panel built the last time this section was shown.
        (_hoursError.Parent as Panel)?.Children.Remove(_hoursError);
        panel.Children.Add(_hoursError);

        void Save()
        {
            if (_loading || Store is not { } store)
            {
                return;
            }
            var (s, e) = (start.Time, end.Time);
            if (e <= s)
            {
                _hoursError.IsOpen = true;
                return;
            }
            _hoursError.IsOpen = false;
            var chosen = new WorkingHours(checks.Where(c => c.Box.IsChecked == true).Select(c => c.Day).ToHashSet(),
                TimeOnly.FromTimeSpan(s), TimeOnly.FromTimeSpan(e));
            try
            {
                store.SetWorkingHours(chosen);
                Log.Information("Working hours set: {Hours}", chosen.Serialize());
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Saving working hours failed");
            }
        }
        foreach (var (_, box) in checks)
        {
            box.Click += (_, _) => Save();
        }
        start.TimeChanged += Save;
        end.TimeChanged += Save;
        _loading = false;
        return panel;
    }

    // ---- Appearance (calendar-shading R7, timezone-handling R8) ----

    private SpacedStack Appearance()
    {
        var panel = Section("Appearance", null);
        var weekNumbers = new Wpf.Ui.Controls.ToggleSwitch
        {
            Content = "Show week numbers",
            IsChecked = Store?.GetUiFlag(WeekNumbersFlag) == true,
        };
        weekNumbers.Click += (_, _) =>
        {
            var on = weekNumbers.IsChecked == true;
            Store?.SetUiFlag(WeekNumbersFlag, on);
            Log.Information("Week numbers {State}", on ? "on" : "off");
            Changed?.Invoke();
        };
        panel.Children.Add(weekNumbers);
        panel.Children.Add(new TextBlock
        {
            Text = "ISO 8601 numbering. A week that starts on Sunday shows the number of its Monday.",
            Style = Theme.Style("CaptionTextBlockStyle"),
            Foreground = Theme.Brush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        // The secondary timezone ruler, saved with the active calendar set (timezone-handling R8, calendar-sets R11);
        // a change can mark the set "(modified)", so the selector refreshes too.
        if (Store is { } store)
        {
            panel.Children.Add(RulerPicker.Build(store, () => SetsChanged?.Invoke()));
        }
        return panel;
    }

    /// <summary>Whether week numbers are on (read by the window before each render).</summary>
    public static bool WeekNumbersOn(LocalStore? store) => store?.GetUiFlag(WeekNumbersFlag) == true;

    private static SpacedStack Section(string title, string? description)
    {
        var panel = new SpacedStack { Spacing = 12, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        var heading = new TextBlock { Text = title, Style = Theme.Style("BodyStrongTextBlockStyle") };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(heading);
        if (description is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = Theme.Brush("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        return panel;
    }
}
