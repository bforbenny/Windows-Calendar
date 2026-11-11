using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Views;

/// <summary>
/// The event's timezone (timezone-handling R7): a picker under the times, and a "Separate end timezone" toggle with a
/// second picker for the end. The time pickers show wall-clock times in these zones, and choosing another zone keeps
/// them (09:00 stays 09:00, in the new zone). A floating CalDAV event, or a custom or unknown zone, shows an extra
/// entry that keeps it, with its times in the Windows timezone. Hidden for all-day events; disabled for read-only
/// events. At This event the occurrence gets its own zone; at This and following the new series does (slice 4).
/// </summary>
internal sealed partial class EventEditor
{
    private static readonly object FloatingZone = new(), CustomZone = new();
    private readonly ComboBox _startZone = RulerPicker.ZoneBox("Timezone");
    private readonly Wpf.Ui.Controls.ToggleSwitch _separateEnd = new() { Content = "Separate end timezone" };
    private readonly ComboBox _endZone = RulerPicker.ZoneBox("End timezone");
    private StackPanel _startZoneField = null!;
    private StackPanel _endZoneField = null!;
    // The pickers' tags as saved: a Windows zone id or an extra entry; the end's is null when it is the start's.
    private object? _savedStart;
    private object? _savedEnd;

    private void BuildZones(SpacedStack form)
    {
        _startZoneField = ViewParts.Field("Timezone", _startZone);
        _endZoneField = ViewParts.Field("End timezone", _endZone);
        form.Children.Add(_startZoneField);
        form.Children.Add(_separateEnd);
        form.Children.Add(_endZoneField);
        _startZone.SelectionChanged += (_, _) => ZonePicked();
        _endZone.SelectionChanged += (_, _) => ZonePicked();
        _separateEnd.Click += (_, _) =>
        {
            // The end starts in the start's zone, so turning the toggle on changes nothing yet.
            if (_separateEnd.IsChecked == true && ZoneTag(_startZone) is string start)
            {
                var loading = _loading;
                _loading = true;
                Select(_endZone, start);
                _loading = loading;
            }
            ZonePicked();
        };
    }

    private void ZonePicked()
    {
        if (_loading)
        {
            return;
        }
        UpdateZoneControls();
        ShowZoneNote();
        Save();
    }

    /// <summary>
    /// Shows <paramref name="item"/>'s own zones as saved; the Windows timezone for a new event. A CalDAV occurrence
    /// with an override shows the override's until the series is chosen.
    /// </summary>
    private void ShowZones(Item? item)
    {
        var recurrence = _scope is EditScope.Series or EditScope.Following ? null : _recurrence;
        var (start, end, floating) = item is null ? (TimeZoneInfo.Local, null, false) : TimeZoneLabels.OwnZones(item, TimeZoneInfo.Local, recurrence);
        var loading = _loading;
        _loading = true;
        foreach (var extra in _startZone.Items.OfType<ComboBoxItem>().Where(i => i.Tag == FloatingZone || i.Tag == CustomZone).ToList())
        {
            _startZone.Items.Remove(extra);
        }
        _savedStart = start is not null ? TimeZoneLabels.WindowsId(start) : floating ? FloatingZone : CustomZone;
        if (start is null)
        {
            _startZone.Items.Insert(0, new ComboBoxItem { Content = floating ? "Floating (no timezone)" : "Custom (kept)", Tag = _savedStart });
        }
        _savedEnd = end is null ? null : TimeZoneLabels.WindowsId(end);
        RevertZones();
        _loading = loading;
    }

    /// <summary>The pickers back at the saved zones.</summary>
    private void RevertZones()
    {
        var loading = _loading;
        _loading = true;
        Select(_startZone, _savedStart);
        Select(_endZone, _savedEnd ?? (_savedStart is string ? _savedStart : TimeZoneLabels.WindowsId(TimeZoneInfo.Local)));
        _separateEnd.IsChecked = _savedEnd is not null;
        _loading = loading;
    }

    /// <summary>After a save: the zones as stored (an event made all-day is back in the Windows timezone).</summary>
    private void RefreshZones()
    {
        if (_itemId is not null && Store?.GetItem(_itemId) is { } item)
        {
            ShowZones(ZoneItem(item));
        }
        UpdateZoneControls();
    }

    /// <summary>The event whose zones the pickers show: an MS365 occurrence's own event until the series is chosen.</summary>
    private Item ZoneItem(Item item) =>
        _graph && _recurrence is not null && _scope is not (EditScope.Series or EditScope.Following)
        && _occurrenceId is { } id && Store?.GetItem(id) is { } occurrence
            ? occurrence
            : item;

    private void UpdateZoneControls()
    {
        var timed = _allDay.IsChecked != true;
        _startZoneField.Visibility = _separateEnd.Visibility = timed ? Visibility.Visible : Visibility.Collapsed;
        _endZoneField.Visibility = timed && _separateEnd.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _startZone.IsEnabled = !_readOnly;
        _separateEnd.IsEnabled = _endZone.IsEnabled = !_readOnly && ZoneTag(_startZone) is string;
    }

    private static object? ZoneTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag;

    private static void Select(ComboBox box, object? tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, tag));

    private static TimeZoneInfo Zone(ComboBox box) =>
        ZoneTag(box) is string id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Local;

    /// <summary>The zone the start pickers show; the Windows timezone for all-day and the extra entries.</summary>
    private TimeZoneInfo FormStartZone => _allDay.IsChecked == true ? TimeZoneInfo.Local : Zone(_startZone);

    private TimeZoneInfo FormEndZone => _allDay.IsChecked == true ? TimeZoneInfo.Local
        : _separateEnd.IsChecked == true ? Zone(_endZone) : FormStartZone;

    /// <summary>Whether other zones than the saved ones are chosen; an extra entry keeps its zone.</summary>
    private bool ZonesChosen()
    {
        var start = ZoneTag(_startZone);
        var end = _separateEnd.IsChecked == true ? ZoneTag(_endZone) : start;
        return start is string && (!Equals(start, _savedStart) || !Equals(end, _savedEnd ?? _savedStart));
    }

    /// <summary>The start as the pickers show it (a date for all-day).</summary>
    private DateTime FormStart()
    {
        var date = _startDate.SelectedDate?.Date ?? DateTime.Today;
        return _allDay.IsChecked == true ? date : date + _startTime.Time;
    }

    /// <summary>The end as the pickers show it (all-day: the day after the last, exclusive).</summary>
    private DateTime FormEnd()
    {
        var date = _endDate.SelectedDate?.Date ?? _startDate.SelectedDate?.Date ?? DateTime.Today;
        return _allDay.IsChecked == true ? date.AddDays(1) : date + _endTime.Time;
    }

    /// <summary>Shows <paramref name="fields"/>' times (Windows wall-clock times) in the pickers' zones.</summary>
    private void ShowTimes(EventFields fields)
    {
        _allDay.IsChecked = fields.AllDay;
        var start = fields.AllDay ? fields.Start : EventFields.Convert(fields.Start, TimeZoneInfo.Local, FormStartZone);
        var end = fields.AllDay ? fields.End : EventFields.Convert(fields.End, TimeZoneInfo.Local, FormEndZone);
        _startDate.SelectedDate = start.Date;
        _startTime.Time = start.TimeOfDay;
        // All-day end dates are shown inclusive.
        _endDate.SelectedDate = fields.AllDay ? end.Date.AddDays(-1) : end.Date;
        _endTime.Time = end.TimeOfDay;
        _shownStart = start;
    }

    /// <summary>
    /// When the pickers show another zone than the Windows one, the times in the Windows timezone, e.g. "14:00 –
    /// 15:00 London time" (timezone-handling R9, turned round now that the pickers show the event's own zone).
    /// </summary>
    private void ShowZoneNote()
    {
        var local = TimeZoneInfo.Local;
        var startZone = FormStartZone;
        var endZone = FormEndZone;
        DateTimeOffset At(DateTime wall, TimeZoneInfo zone)
        {
            var w = EventFields.Convert(wall, zone, local);
            return new DateTimeOffset(w, local.GetUtcOffset(w));
        }
        var text = _allDay.IsChecked == true || (TimeZoneLabels.Same(startZone, local) && TimeZoneLabels.Same(endZone, local)) ? null
            : TimeZoneLabels.OriginalTime(At(FormStart(), startZone), At(FormEnd(), endZone), local.Id, null,
                TimeZoneLabels.Same(startZone, local) ? endZone : startZone);
        _zoneNote.Text = text ?? "";
        _zoneNote.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
