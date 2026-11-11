using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace WindowsCalendar.Views;

/// <summary>
/// A time of day (WPF has no time picker; calendar-views R6): an hour drop-down of 24 entries in the Windows time
/// format and a minute drop-down in 5-minute steps. A minute off the steps is shown as its own entry, never rounded.
/// </summary>
internal sealed class TimeBox : UserControl
{
    private readonly ComboBox _hour = new() { MinWidth = 88 };
    private readonly ComboBox _minute = new() { MinWidth = 72, Margin = new Thickness(4, 0, 0, 0) };
    private TimeSpan _time;
    private bool _setting;

    public TimeBox()
    {
        var twelveHour = !CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H');
        for (var h = 0; h < 24; h++)
        {
            var label = DateTime.Today.AddHours(h).ToString(twelveHour ? "h tt" : "HH", CultureInfo.CurrentCulture);
            _hour.Items.Add(new ComboBoxItem { Content = label, Tag = h });
        }
        _hour.SelectionChanged += (_, _) => ReadPicked();
        _minute.SelectionChanged += (_, _) => ReadPicked();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_hour);
        row.Children.Add(_minute);
        Content = row;
        Show(_time);
    }

    /// <summary>Raised when the time changes, also when set from code.</summary>
    public event Action? TimeChanged;

    public TimeSpan Time
    {
        get => _time;
        set => Set(value);
    }

    private void Set(TimeSpan value)
    {
        var changed = value != _time;
        _time = value;
        Show(value);
        if (changed)
        {
            TimeChanged?.Invoke();
        }
    }

    private void Show(TimeSpan time)
    {
        _setting = true;
        if (MinuteItem(time.Minutes) is null)
        {
            // Rebuilt only from code: a picked minute is always in the list.
            _minute.Items.Clear();
            foreach (var m in Enumerable.Range(0, 12).Select(i => i * 5).Append(time.Minutes).Distinct().Order())
            {
                _minute.Items.Add(new ComboBoxItem { Content = m.ToString("00", CultureInfo.CurrentCulture), Tag = m });
            }
        }
        _hour.SelectedIndex = time.Hours;
        _minute.SelectedItem = MinuteItem(time.Minutes);
        _setting = false;
    }

    private ComboBoxItem? MinuteItem(int minute) =>
        _minute.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == minute);

    private void ReadPicked()
    {
        if (!_setting && _hour.SelectedItem is ComboBoxItem { Tag: int h } && _minute.SelectedItem is ComboBoxItem { Tag: int m })
        {
            Set(new TimeSpan(h, m, 0));
        }
    }
}
