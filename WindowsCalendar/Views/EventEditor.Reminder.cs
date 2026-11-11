using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Serilog;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Reminders;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Views;

/// <summary>
/// The Reminder drop-down (reminders-notifications R5a): one reminder, from the timed or the all-day list. A value
/// from sync that is not in the list shows as an extra entry and is kept. New events take their calendar's default
/// (R5). On a view-only MS365 meeting only the reminder can be changed.
/// </summary>
internal sealed partial class EventEditor
{
    // A new event's reminder still follows its calendar's default and the all-day box.
    private bool _defaultReminder;
    // A view-only meeting: the reminder is the one field that saves.
    private bool _reminderOnly;
    private int _moreCount;

    private void ShowReminder(ReminderChoice current, int more)
    {
        _moreCount = more;
        var allDay = _allDay.IsChecked == true;
        var graph = _itemId is null ? (_calendar.SelectedItem as ComboBoxItem)?.Tag is Collection c && IsGraphCalendar(c) : _graph;
        var values = (allDay ? ReminderChoices.AllDay(graph) : ReminderChoices.Timed).ToList();
        if (!values.Contains(current))
        {
            if (_itemId is null)
            {
                current = DefaultReminder();
            }
            if (!values.Contains(current))
            {
                values.Add(current);
            }
        }
        var loading = _loading;
        _loading = true;
        _reminder.Items.Clear();
        foreach (var value in values)
        {
            _reminder.Items.Add(new ComboBoxItem { Content = ReminderChoices.Label(value, allDay), Tag = value });
        }
        _reminder.SelectedIndex = values.IndexOf(current);
        _loading = loading;
        _moreReminders.Text = more == 1 ? "+1 more reminder (kept)" : $"+{more} more reminders (kept)";
        _moreReminders.Visibility = more > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ReminderChoice ReadReminder() => (_reminder.SelectedItem as ComboBoxItem)?.Tag as ReminderChoice ?? ReminderChoice.None;

    /// <summary>The chosen calendar's default for a timed event; all-day events get none (R5).</summary>
    private ReminderChoice DefaultReminder() =>
        _allDay.IsChecked != true && (_calendar.SelectedItem as ComboBoxItem)?.Tag is Collection c && Store?.GetDefaultReminder(c.Id) is { } minutes
            ? ReminderChoice.Before(minutes)
            : ReminderChoice.None;

    /// <summary>
    /// A view-only meeting's reminder (R5a): written to the event alone, as a PATCH of the reminder fields. For an
    /// occurrence of a series, asks whether it is for this event or the whole series.
    /// </summary>
    private async void SaveReminderOnly()
    {
        if (_asking || Store is not { } store || _itemId is not { } id || store.GetItem(id) is not { } item)
        {
            return;
        }
        var reminder = ReadReminder();
        var zone = TimeZoneInfo.Local;
        try
        {
            Item target = item;
            Item? master = null;
            if (_occurrenceId is { } occurrenceId && store.GetItem(occurrenceId) is { } occurrence)
            {
                _asking = true;
                var scope = await ScopeDialog.AskAsync("Change a repeating event", allowFollowing: false);
                _asking = false;
                if (_itemId != id)
                {
                    return;
                }
                if (scope is null)
                {
                    ShowReminder(GraphEventMapper.Read(occurrence, item, zone).Reminder ?? ReminderChoice.None, 0);
                    return;
                }
                if (scope == EditScope.Occurrence)
                {
                    (target, master) = (occurrence, item);
                }
            }
            if (GraphEvents.EditReminder(target, master, reminder, zone) is { } edited)
            {
                store.UpdateItem(edited);
                Log.Information("Reminder of event {ItemId} changed", target.Id);
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _asking = false;
            Log.Error(ex, "Saving the reminder of event {ItemId} failed", id);
        }
    }
}
