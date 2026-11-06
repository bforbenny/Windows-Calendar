using System;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WindowsCalendar.Core.CalDav;

namespace WindowsCalendar.Views;

/// <summary>
/// The editor's repeat controls (recurring-events R3 interim): a Repeat dropdown, and below it the interval, weekday
/// toggles (Weekly), day or weekday of the month (Monthly) and the end. It only shows and reads a
/// <see cref="RepeatRule"/>; the editor decides when to save.
/// </summary>
internal sealed partial class RepeatPanel : StackPanel
{
    private readonly ComboBox _repeat = new() { Header = "Repeat", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _interval = new()
    {
        Header = "Every",
        Minimum = 1,
        Maximum = 99,
        Value = 1,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        MinWidth = 120,
    };
    private readonly TextBlock _unit = new() { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
    private readonly StackPanel _weekdayRow = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly ToggleButton[] _weekdays;
    private readonly ComboBox _monthly = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _ends = new() { Header = "Ends", MinWidth = 140 };
    private readonly NumberBox _count = new()
    {
        Minimum = 1,
        Maximum = 999,
        Value = 10,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        VerticalAlignment = VerticalAlignment.Bottom,
    };
    private readonly TextBlock _times = new() { Text = "times", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
    private readonly CalendarDatePicker _until = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly StackPanel _detail = new() { Spacing = 8 };
    private readonly Button _apply = new() { Content = "Apply", HorizontalAlignment = HorizontalAlignment.Right };

    private DateOnly _start;
    private bool _loading;

    public RepeatPanel()
    {
        Spacing = 8;
        foreach (var label in new[] { "Doesn't repeat", "Daily", "Weekly", "Monthly", "Yearly" })
        {
            _repeat.Items.Add(label);
        }
        foreach (var label in new[] { "Never", "After", "On date" })
        {
            _ends.Items.Add(label);
        }
        _apply.Style = Theme.Style("AccentButtonStyle");

        // Weekdays in the culture's week order, e.g. M T W T F S S.
        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        _weekdays = Enumerable.Range(0, 7)
            .Select(i => (DayOfWeek)(((int)format.FirstDayOfWeek + i) % 7))
            .Select(day =>
            {
                var toggle = new ToggleButton { Content = format.GetShortestDayName(day), Tag = day, Width = 40, Padding = new Thickness(0, 5, 0, 6) };
                ToolTipService.SetToolTip(toggle, format.GetDayName(day));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, format.GetDayName(day));
                toggle.Click += (_, _) =>
                {
                    // At least one weekday stays selected.
                    if (!_weekdays!.Any(t => t.IsChecked == true))
                    {
                        toggle.IsChecked = true;
                    }
                    Changed();
                };
                _weekdayRow.Children.Add(toggle);
                return toggle;
            })
            .ToArray();

        var every = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        every.Children.Add(_interval);
        every.Children.Add(_unit);
        var ends = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        ends.Children.Add(_ends);
        ends.Children.Add(_count);
        ends.Children.Add(_times);
        ends.Children.Add(_until);
        _detail.Children.Add(every);
        _detail.Children.Add(_weekdayRow);
        _detail.Children.Add(_monthly);
        _detail.Children.Add(ends);
        Children.Add(_repeat);
        Children.Add(_detail);
        Children.Add(_apply);

        _repeat.SelectionChanged += (_, _) =>
        {
            if (Frequency == RepeatFrequency.Weekly && !_weekdays.Any(t => t.IsChecked == true))
            {
                Toggle(_start.DayOfWeek).IsChecked = true;
            }
            Changed();
        };
        _interval.ValueChanged += (_, _) => Changed();
        _monthly.SelectionChanged += (_, _) => Changed();
        _ends.SelectionChanged += (_, _) => Changed();
        _count.ValueChanged += (_, _) => Changed();
        _until.DateChanged += (_, _) => Changed();
        _apply.Click += (_, _) => ApplyClicked?.Invoke();
        ApplyVisible = false;
    }

    /// <summary>Raised when the user changes a control (not when <see cref="Show"/> sets them).</summary>
    public event Action? RuleChanged;

    public event Action? ApplyClicked;

    public bool ApplyVisible
    {
        set => _apply.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows <paramref name="rule"/> for a series starting on <paramref name="start"/>.</summary>
    public void Show(RepeatRule rule, DateOnly start)
    {
        _loading = true;
        _start = start;
        _repeat.SelectedIndex = (int)rule.Frequency;
        _interval.Value = rule.Interval;
        foreach (var toggle in _weekdays)
        {
            var day = (DayOfWeek)toggle.Tag;
            toggle.IsChecked = rule.Frequency == RepeatFrequency.Weekly ? rule.Weekdays.Contains(day) : day == start.DayOfWeek;
        }
        BuildMonthly(rule.MonthlyWeek);
        _ends.SelectedIndex = rule.Count is not null ? 1 : rule.Until is not null ? 2 : 0;
        _count.Value = rule.Count ?? 10;
        _until.Date = (rule.Until ?? start.AddMonths(3)).ToDateTime(TimeOnly.MinValue);
        ApplyVisible = false;
        ShowDetail();
        _loading = false;
    }

    /// <summary>
    /// The start moved: the monthly choices follow the new date, keeping "day" or "weekday" where it still exists.
    /// Not raised as a change; the editor saves the start itself.
    /// </summary>
    public void SetStart(DateOnly start)
    {
        if (start == _start)
        {
            return;
        }
        _loading = true;
        var week = _monthly.SelectedItem is ComboBoxItem { Tag: int t } && t != 0 ? t : (int?)null;
        _start = start;
        BuildMonthly(week is -1 ? -1 : week is not null ? RepeatRule.WeekOfMonth(start) : null);
        if (Frequency != RepeatFrequency.Weekly)
        {
            foreach (var toggle in _weekdays)
            {
                toggle.IsChecked = (DayOfWeek)toggle.Tag == start.DayOfWeek;
            }
        }
        _loading = false;
    }

    /// <summary>The rule the controls show.</summary>
    public RepeatRule Read()
    {
        var frequency = Frequency;
        if (frequency == RepeatFrequency.None)
        {
            return RepeatRule.None;
        }
        var days = frequency == RepeatFrequency.Weekly
            ? _weekdays.Where(t => t.IsChecked == true).Select(t => (DayOfWeek)t.Tag).ToList()
            : [];
        int? week = frequency == RepeatFrequency.Monthly && _monthly.SelectedItem is ComboBoxItem { Tag: int t } && t != 0 ? t : null;
        int? count = _ends.SelectedIndex == 1 ? Whole(_count.Value) : null;
        DateOnly? until = _ends.SelectedIndex == 2 && _until.Date is { } date ? DateOnly.FromDateTime(date.Date) : null;
        return new RepeatRule(frequency, Whole(_interval.Value), days, week, count, until);
    }

    public void SetEnabled(bool enabled)
    {
        foreach (var control in new Control[] { _repeat, _interval, _monthly, _ends, _count, _until, _apply }.Concat(_weekdays))
        {
            control.IsEnabled = enabled;
        }
    }

    private RepeatFrequency Frequency => (RepeatFrequency)Math.Max(0, _repeat.SelectedIndex);

    private static int Whole(double value) => double.IsNaN(value) ? 1 : Math.Max(1, (int)value);

    private ToggleButton Toggle(DayOfWeek day) => _weekdays.First(t => (DayOfWeek)t.Tag == day);

    private void Changed()
    {
        ShowDetail();
        if (!_loading)
        {
            RuleChanged?.Invoke();
        }
    }

    /// <summary>"On day 6", "On the 1st Friday" (weeks 1–4) and "On the last Friday" (if it is), from the start.</summary>
    private void BuildMonthly(int? week)
    {
        var name = CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(_start.DayOfWeek);
        var n = RepeatRule.WeekOfMonth(_start);
        _monthly.Items.Clear();
        _monthly.Items.Add(new ComboBoxItem { Content = $"On day {_start.Day}", Tag = 0 });
        if (n <= 4)
        {
            _monthly.Items.Add(new ComboBoxItem { Content = $"On the {Ordinal(n)} {name}", Tag = n });
        }
        if (RepeatRule.IsLastWeek(_start))
        {
            _monthly.Items.Add(new ComboBoxItem { Content = $"On the last {name}", Tag = -1 });
        }
        var index = _monthly.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (int)i.Tag == (week ?? 0));
        _monthly.SelectedIndex = Math.Max(0, index);
    }

    private void ShowDetail()
    {
        var frequency = Frequency;
        _detail.Visibility = frequency == RepeatFrequency.None ? Visibility.Collapsed : Visibility.Visible;
        _weekdayRow.Visibility = frequency == RepeatFrequency.Weekly ? Visibility.Visible : Visibility.Collapsed;
        _monthly.Visibility = frequency == RepeatFrequency.Monthly ? Visibility.Visible : Visibility.Collapsed;
        _count.Visibility = _times.Visibility = _ends.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        _until.Visibility = _ends.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        var unit = frequency switch
        {
            RepeatFrequency.Daily => "day",
            RepeatFrequency.Weekly => "week",
            RepeatFrequency.Monthly => "month",
            _ => "year",
        };
        _unit.Text = Whole(_interval.Value) > 1 ? unit + "s" : unit;
    }

    private static string Ordinal(int n) => n switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => $"{n}th",
    };
}
