using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WindowsCalendar.Core.Views;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// Keyboard shortcuts and the keyboard selection (keyboard-navigation interim): F1-F4 views, T today, F5 sync,
/// Ctrl+G go to date, PgUp / PgDn, ? cheat sheet; Arrows select, Enter opens, Del deletes, Esc clears, and
/// Alt(+Shift)+Arrows move or resize the selected event through the drag path (R3).
/// </summary>
public sealed partial class MainWindow
{
    private const VirtualKey QuestionKey = (VirtualKey)191; // VK_OEM_2: "/" and, with Shift, "?"

    /// <summary>Shown in the cheat sheet, in this order.</summary>
    private static readonly (string Keys, string Action)[] Shortcuts =
    [
        ("F1 / F2 / F3 / F4", "Week / Month / Agenda / Tasks"),
        ("T", "Go to today"),
        ("Ctrl+G", "Go to date"),
        ("PgUp / PgDn", "Scroll back / forward one screen"),
        ("Arrows", "Select an event: Up/Down within the day, Left/Right across days"),
        ("Enter", "Open the selected event or task"),
        ("Del", "Delete the selected event"),
        ("Alt+Up / Alt+Down", "Move the selected event 15 minutes"),
        ("Alt+Left / Alt+Right", "Move the selected event one day"),
        ("Alt+Shift+Up / Down", "Shorten / lengthen the selected event by 15 minutes"),
        ("Esc", "Close the panel or Settings, or clear the selection"),
        ("Ctrl+N", "New event (New task in the Tasks view)"),
        ("Ctrl+Z", "Undo the last move"),
        ("F5", "Sync now"),
        ("Ctrl+,", "Settings"),
        ("Ctrl+B", "Hide or show the sidebar"),
        ("?", "This list"),
        ("Hold Ctrl", "Show the keys on the buttons"),
    ];

    private IReadOnlyList<DisplayEvent> _shownEvents = [];
    private string? _selectedKey;
    private DateOnly _selectedDay;

    /// <summary>Handles a shortcut key; false when the key is not one, or not available where focus is.</summary>
    private bool HandleShortcut(VirtualKey key)
    {
        var ctrl = IsControlDown();
        var shift = IsDown(VirtualKey.Shift);
        var alt = IsDown(VirtualKey.Menu);
        var plain = !ctrl && !shift && !alt;

        if (plain && key is >= VirtualKey.F1 and <= VirtualKey.F4)
        {
            ViewSwitcher.SelectedItem = ViewSwitcher.Items[key - VirtualKey.F1];
            return true;
        }
        if (plain && key == VirtualKey.F5)
        {
            ((App)Application.Current).SyncNow();
            return true;
        }
        if (ctrl && !alt && key == VirtualKey.G)
        {
            ShowGoToDate();
            return true;
        }
        if (!SingleKeysAllowed())
        {
            return false;
        }
        var calendar = _view != ViewKind.Tasks;
        switch (key)
        {
            case VirtualKey.T when plain && calendar:
                ScrollToToday();
                return true;
            case QuestionKey when shift && !ctrl && !alt:
                ShowCheatSheet();
                return true;
            case VirtualKey.PageUp or VirtualKey.PageDown when plain && calendar:
                CurrentView.Page(key == VirtualKey.PageUp ? -1 : 1);
                return true;
            case VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right when calendar && !ctrl:
                if (alt)
                {
                    MoveSelected(key, shift);
                    return true;
                }
                if (!shift)
                {
                    MoveSelection(key);
                    return true;
                }
                return false;
            // A focused button or checkbox keeps its own Enter.
            case VirtualKey.Enter when plain && FocusManager.GetFocusedElement(Content.XamlRoot) is not ButtonBase
                && Selected() is { } open:
                OpenItem(open);
                return true;
            case VirtualKey.Delete when plain && Selected() is { IsTask: false } doomed:
                ShowEventEditor();
                _editor.OpenAndDelete(doomed.ItemId, doomed.Recurrence, _collections);
                return true;
            case VirtualKey.Escape when !_editor.IsOpen && !_taskEditor.IsOpen && _selectedKey is not null:
                _selectedKey = EventCard.SelectedKey = null;
                RenderView();
                return true;
            default:
                return false;
        }
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// R4: single keys, arrows, Enter and Del are left alone while typing or in a drop-down, and inside the editor
    /// panel or Settings. The view tabs do not keep their arrow keys: F1-F4 switch views.
    /// </summary>
    private bool SingleKeysAllowed()
    {
        if (SettingsHost.Visibility == Visibility.Visible)
        {
            return false;
        }
        var focused = FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject;
        for (var e = focused; e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (e is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or ComboBox or CalendarDatePicker or TimePicker
                or DatePicker || e == EditorHost)
            {
                return false;
            }
        }
        return true;
    }

    // ---- Selection ----

    private DisplayEvent? Selected() => _selectedKey is null ? null : _shownEvents.FirstOrDefault(e => e.Key == _selectedKey);

    private void Select(DisplayEvent e, DateOnly day, bool reveal)
    {
        _selectedKey = EventCard.SelectedKey = e.Key;
        _selectedDay = day;
        RenderView();
        if (reveal)
        {
            CurrentView.Reveal(day, e.InAllDayLane || DateOnly.FromDateTime(e.Start) != day ? null : e.Start);
        }
    }

    /// <summary>Arrows: the first press selects (today's first item, else the first from the day in view).</summary>
    private void MoveSelection(VirtualKey key)
    {
        var nav = new SelectionNav(_shownEvents.Select(e => new Selectable(e.Key, e.FirstDay, e.LastDay, e.InAllDayLane, e.Start)));
        var day = _selectedKey is { } current ? nav.DayOf(current, _selectedDay) : null;
        var next = day is { } d
            ? nav.Move(_selectedKey!, d, key switch
            {
                VirtualKey.Up => SelectionMove.Up,
                VirtualKey.Down => SelectionMove.Down,
                VirtualKey.Left => SelectionMove.Left,
                _ => SelectionMove.Right,
            })
            : nav.Initial(_today, _visibleDay ?? _today);
        if (next is { } n && _shownEvents.FirstOrDefault(e => e.Key == n.Item.Key) is { } e)
        {
            Select(e, n.Day, reveal: true);
        }
        else if (day is { } stay && Selected() is { } same)
        {
            // Nowhere further to go: keep the selection in view.
            CurrentView.Reveal(stay, same.InAllDayLane ? null : same.Start);
        }
    }

    private void OpenItem(DisplayEvent e)
    {
        if (e.IsTask)
        {
            OpenTask(e.ItemId);
            return;
        }
        ShowEventEditor();
        _editor.OpenExisting(e.ItemId, e.Recurrence, _collections);
    }

    /// <summary>
    /// R3: Alt+Up/Down move 15 minutes, Alt+Left/Right one day, Alt+Shift+Up/Down shorten / lengthen by 15 minutes
    /// (at least 15 minutes long). All-day events only move by day. Goes through the drag path.
    /// </summary>
    private void MoveSelected(VirtualKey key, bool shift)
    {
        if (Selected() is not { } e || !e.Editable || e.IsTask)
        {
            return;
        }
        var step = TimeSpan.FromMinutes(15);
        var (start, end) = (e.Start, e.End);
        var vertical = key is VirtualKey.Up or VirtualKey.Down;
        if (vertical && e.AllDay || !vertical && shift)
        {
            return;
        }
        var sign = key is VirtualKey.Up or VirtualKey.Left ? -1 : 1;
        if (!vertical)
        {
            (start, end) = (start.AddDays(sign), end.AddDays(sign));
        }
        else if (shift)
        {
            end += sign * step;
            if (end - start < step)
            {
                return;
            }
        }
        else
        {
            (start, end) = (start + sign * step, end + sign * step);
        }
        if (!vertical)
        {
            _selectedDay = _selectedDay.AddDays(sign);
        }
        OnEventDropped(e, new DragTarget(start, end, e.AllDay, default));
    }

    // ---- Go to date and the cheat sheet ----

    /// <summary>Ctrl+G: a month calendar under the period title; picking a day scrolls there (Tasks switches to Week).</summary>
    private void ShowGoToDate()
    {
        var shown = _visibleDay ?? _today;
        var calendar = new CalendarView { SelectionMode = CalendarViewSelectionMode.Single };
        calendar.SetDisplayDate(new DateTimeOffset(shown.ToDateTime(TimeOnly.MinValue)));
        var flyout = new Flyout { Content = calendar, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        calendar.SelectedDatesChanged += (_, args) =>
        {
            if (args.AddedDates.Count == 0)
            {
                return;
            }
            flyout.Hide();
            var day = DateOnly.FromDateTime(args.AddedDates[0].Date);
            if (_view == ViewKind.Tasks)
            {
                _pendingDay = day;
                ViewSwitcher.SelectedItem = ViewSwitcher.Items[0];
            }
            else
            {
                CurrentView.ScrollTo(day, toNow: false);
            }
        };
        flyout.Opened += (_, _) => calendar.Focus(FocusState.Keyboard);
        flyout.ShowAt(PeriodTitle);
    }

    /// <summary>?: the shortcuts that are built, in a dialog.</summary>
    private async void ShowCheatSheet()
    {
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < Shortcuts.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock { Text = Shortcuts[i].Keys, Style = Theme.Style("BodyStrongTextBlockStyle") };
            var action = new TextBlock { Text = Shortcuts[i].Action, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(keys, i);
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(keys);
            grid.Children.Add(action);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Keyboard shortcuts",
            Content = new ScrollViewer { Content = grid },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // Another dialog is already open.
            Serilog.Log.Information(ex, "Cheat sheet not shown");
        }
    }
}
