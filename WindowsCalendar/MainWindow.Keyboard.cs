using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WindowsCalendar.Core.Views;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// Keyboard shortcuts and the keyboard selection (keyboard-navigation interim): F1-F4 views, T today, F5 sync,
/// Ctrl+G go to date, Ctrl+F / Ctrl+E search, PgUp / PgDn, ? cheat sheet; Arrows select, Enter opens, Del deletes, Ctrl+D duplicates, Ctrl+J joins, Esc clears, and
/// Alt(+Shift)+Arrows move or resize the selected event through the drag path (R3).
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>F1-F4, in the view switcher's order.</summary>
    private static readonly ViewKind[] ViewKeys = [ViewKind.Week, ViewKind.Month, ViewKind.Agenda, ViewKind.Tasks];

    /// <summary>Shown in the cheat sheet, in this order.</summary>
    private static readonly (string Keys, string Action)[] Shortcuts =
    [
        ("F1 / F2 / F3 / F4", "Week / Month / Agenda / Tasks"),
        ("T", "Go to today"),
        ("Ctrl+G", "Go to date"),
        ("Ctrl+F / Ctrl+E", "Search events and tasks"),
        ("PgUp / PgDn", "Scroll back / forward one screen"),
        ("Arrows", "Select an event: Up/Down within the day, Left/Right across days"),
        ("Enter", "Open the selected event or task"),
        ("Del", "Delete the selected event"),
        ("Ctrl+D", "Duplicate the selected event"),
        ("Ctrl+drag", "Copy an event to where it is dropped"),
        ("Ctrl+J", "Join the selected event's online meeting"),
        ("Ctrl+Shift+J", "Join the meeting on now or starting within 10 minutes"),
        ("Alt+Up / Alt+Down", "Move the selected event 15 minutes"),
        ("Alt+Left / Alt+Right", "Move the selected event one day"),
        ("Alt+Shift+Up / Down", "Shorten / lengthen the selected event by 15 minutes"),
        ("Esc", "Close the panel or Settings, or clear the selection"),
        ("Ctrl+N", "Quick add"),
        ("Ctrl+Z", "Undo the last move, duplicate or quick add"),
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
    private bool HandleShortcut(Key key)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var plain = !ctrl && !shift && !alt;

        if (plain && key is >= Key.F1 and <= Key.F4)
        {
            SwitchView(ViewKeys[key - Key.F1]);
            return true;
        }
        if (plain && key == Key.F5)
        {
            ((App)Application.Current).SyncNow();
            return true;
        }
        if (ctrl && !alt && key == Key.G)
        {
            ShowGoToDate();
            return true;
        }
        if (ctrl && shift && !alt && key == Key.J)
        {
            JoinNext();
            return true;
        }
        if (ctrl && !shift && !alt && key is Key.F or Key.E && SettingsHost.Visibility != Visibility.Visible)
        {
            FocusSearch();
            return true;
        }
        if (!SingleKeysAllowed())
        {
            return false;
        }
        var calendar = _view != ViewKind.Tasks;
        switch (key)
        {
            case Key.T when plain && calendar:
                ScrollToToday();
                return true;
            case Key.OemQuestion when shift && !ctrl && !alt:
                ShowCheatSheet();
                return true;
            case Key.PageUp or Key.PageDown when plain && calendar:
                CurrentView.Page(key == Key.PageUp ? -1 : 1);
                return true;
            case Key.Up or Key.Down or Key.Left or Key.Right when calendar && !ctrl:
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
            case Key.Enter when plain && Keyboard.FocusedElement is not ButtonBase && Selected() is { } open:
                OpenItem(open);
                return true;
            case Key.D when ctrl && !shift && !alt && calendar && Selected() is { CanDuplicate: true } original:
                Duplicate(original, null);
                return true;
            case Key.J when ctrl && !shift && !alt && calendar && Selected() is { Join: not null }:
                JoinSelected();
                return true;
            case Key.Delete when plain && Selected() is { IsTask: false } doomed:
                ShowEventEditor();
                _editor.OpenAndDelete(doomed.ItemId, doomed.Recurrence, _collections);
                return true;
            case Key.Escape when !_editor.IsOpen && !_taskEditor.IsOpen && _selectedKey is not null:
                _selectedKey = EventCard.SelectedKey = null;
                RenderView();
                return true;
            default:
                return false;
        }
    }

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
        for (var e = Keyboard.FocusedElement as DependencyObject; e is not null;
            e = (e is Visual ? VisualTreeHelper.GetParent(e) : null) ?? LogicalTreeHelper.GetParent(e))
        {
            if (e is TextBoxBase or PasswordBox or ComboBox or DatePicker or Calendar || e == EditorHost)
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
    private void MoveSelection(Key key)
    {
        var nav = new SelectionNav(_shownEvents.Select(e => new Selectable(e.Key, e.FirstDay, e.LastDay, e.InAllDayLane, e.Start)));
        var day = _selectedKey is { } current ? nav.DayOf(current, _selectedDay) : null;
        var next = day is { } d
            ? nav.Move(_selectedKey!, d, key switch
            {
                Key.Up => SelectionMove.Up,
                Key.Down => SelectionMove.Down,
                Key.Left => SelectionMove.Left,
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
    private void MoveSelected(Key key, bool shift)
    {
        if (Selected() is not { } e || !e.Editable || e.IsTask)
        {
            return;
        }
        var step = TimeSpan.FromMinutes(15);
        var (start, end) = (e.Start, e.End);
        var vertical = key is Key.Up or Key.Down;
        if (vertical && e.AllDay || !vertical && shift)
        {
            return;
        }
        var sign = key is Key.Up or Key.Left ? -1 : 1;
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
        var calendar = new Calendar { SelectionMode = CalendarSelectionMode.SingleDate, DisplayDate = shown.ToDateTime(TimeOnly.MinValue) };
        var popup = new Popup
        {
            Child = new Border
            {
                Child = calendar,
                Background = Theme.Brush("SurfaceBaseBrush"),
                BorderBrush = Theme.Brush("DividerBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
            },
            PlacementTarget = PeriodTitle,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        calendar.SelectedDatesChanged += (_, args) =>
        {
            if (args.AddedItems.Count == 0 || args.AddedItems[0] is not DateTime picked)
            {
                return;
            }
            popup.IsOpen = false;
            var day = DateOnly.FromDateTime(picked);
            if (_view == ViewKind.Tasks)
            {
                _pendingDay = day;
                SwitchView(ViewKind.Week);
            }
            else
            {
                CurrentView.ScrollTo(day, toNow: false);
            }
        };
        popup.Opened += (_, _) => calendar.Focus();
        popup.IsOpen = true;
    }

    /// <summary>?: the shortcuts that are built, in a dialog.</summary>
    private async void ShowCheatSheet()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < Shortcuts.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock { Text = Shortcuts[i].Keys, Style = Theme.Style("BodyStrongTextBlockStyle"), Margin = new Thickness(0, 0, 24, 6) };
            var action = new TextBlock { Text = Shortcuts[i].Action, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            Grid.SetRow(keys, i);
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(keys);
            grid.Children.Add(action);
        }
        var dialog = new Wpf.Ui.Controls.ContentDialog(DialogHost)
        {
            Title = "Keyboard shortcuts",
            Content = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "Close",
            DefaultButton = Wpf.Ui.Controls.ContentDialogButton.Close,
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
