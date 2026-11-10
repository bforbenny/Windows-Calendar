using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;

namespace WindowsCalendar.Views;

/// <summary>
/// A time of day for the editor (WPF has no time picker): a drop-down of 15-minute steps that also takes a typed time,
/// read when Enter is pressed or focus leaves. An unreadable entry shows the previous time again.
/// </summary>
internal sealed class TimeBox : ComboBox
{
    private TimeSpan _time;
    private bool _setting;

    public TimeBox()
    {
        IsEditable = true;
        MinWidth = 110;
        for (var i = 0; i < 96; i++)
        {
            Items.Add(Format(TimeSpan.FromMinutes(15 * i)));
        }
        SelectionChanged += (_, _) =>
        {
            if (!_setting && SelectedIndex >= 0)
            {
                Set(TimeSpan.FromMinutes(15 * SelectedIndex));
            }
        };
        LostKeyboardFocus += (_, _) => ReadTyped();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ReadTyped();
            }
        };
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
        _setting = true;
        Text = Format(value);
        _setting = false;
        if (changed)
        {
            TimeChanged?.Invoke();
        }
    }

    private void ReadTyped()
    {
        if (!IsEditable || Text == Format(_time))
        {
            return;
        }
        Set(DateTime.TryParse(Text, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out var parsed)
            ? parsed.TimeOfDay
            : _time);
    }

    private static string Format(TimeSpan time) => DateTime.Today.Add(time).ToString("t", CultureInfo.CurrentCulture);
}
