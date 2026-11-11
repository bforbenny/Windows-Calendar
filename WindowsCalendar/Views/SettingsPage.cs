using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Serilog;
using WindowsCalendar.Core.Store;
using InfoBar = Wpf.Ui.Controls.InfoBar;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace WindowsCalendar.Views;

/// <summary>
/// The Settings page (settings-page interim): a section list on the left, the section's settings on the right.
/// First slice: Working hours and Appearance (week numbers). Changes apply at once and are stored locally.
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

    /// <summary>Raised by Back; Esc is handled by the window.</summary>
    public event Action? BackRequested;

    private static LocalStore? Store => ((App)Application.Current).Store;

    /// <summary>Shows the page on its first section, read fresh from the store.</summary>
    public void Open()
    {
        if (_sections.SelectedIndex == 0)
        {
            ShowSection();
        }
        else
        {
            _sections.SelectedIndex = 0;
        }
    }

    private void ShowSection() =>
        _content.Content = _sections.SelectedIndex == 1 ? Appearance() : WorkingHoursSection();

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

    // ---- Appearance (calendar-shading R7) ----

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
