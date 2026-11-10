using System;
using System.Windows;
using Serilog;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar;

/// <summary>
/// The toolbar's New split button (calendar-views and tasks interim): New event, or New task in the Tasks view; the
/// drop-down offers both.
/// </summary>
public sealed partial class MainWindow
{
    // The day in the middle of the current view, from VisibleDayChanged.
    private DateOnly? _visibleDay;

    private void OnNew(object sender, RoutedEventArgs e) => NewDefault();

    /// <summary>The New button's main action, also Ctrl+N: New task in the Tasks view, else New event.</summary>
    private void NewDefault()
    {
        if (_view == ViewKind.Tasks)
        {
            NewTask();
        }
        else
        {
            NewEvent();
        }
    }

    /// <summary>The editors' heading drop-down: the new item, not yet created, is dropped and the title carried over.</summary>
    private void SetupNewSwitch()
    {
        _editor.SwitchRequested += title =>
        {
            _editor.Hide();
            NewTask(title);
        };
        _taskEditor.SwitchRequested += title =>
        {
            _taskEditor.Hide();
            NewEvent(title);
        };
    }

    private void OnNewEvent(object sender, RoutedEventArgs e) => NewEvent();

    private void OnNewTask(object sender, RoutedEventArgs e) => NewTask();

    private void OnNewMenuOpening(object sender, RoutedEventArgs e) => NewTaskItem.IsEnabled = NewTaskList() is not null;

    /// <summary>A 1-hour event at the next full hour, on today when it is in view, else on the day in the middle.</summary>
    private void NewEvent(string title = "")
    {
        var day = _visibleDay is { } middle && !TodayInView(middle) ? middle : _today;
        var hour = Math.Min(DateTime.Now.Hour + 1, 23);
        ShowEventEditor();
        _editor.CanSwitchToTask = NewTaskList() is not null;
        _editor.OpenNew(day.ToDateTime(new TimeOnly(hour, 0)), false, _collections, title);
    }

    /// <summary>Whether today is on screen, judged from the middle day: Week and Agenda show 7 days, Month 5 weeks.</summary>
    private bool TodayInView(DateOnly middle) =>
        Math.Abs(_today.DayNumber - middle.DayNumber) <= (_view == ViewKind.Month ? 17 : 3);

    private void NewTask(string title = "")
    {
        if (NewTaskList() is not { } list)
        {
            Log.Information("New task not opened: no Microsoft To Do list to add it to");
            return;
        }
        if (_editor.IsOpen)
        {
            _editor.Close();
        }
        EditorHost.Child = _taskEditor;
        _taskEditor.OpenNew(list, title);
    }
}
