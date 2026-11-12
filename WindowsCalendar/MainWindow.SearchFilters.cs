using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;
using WindowsCalendar.Views;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using TextBox = Wpf.Ui.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;

namespace WindowsCalendar;

/// <summary>
/// Search filters (search-filters R1, R2, R8, R9): the chip row above the results, the inline Filters panel (type,
/// calendar, date range, attendee, location) and the filter state, which lives on the window for the session only.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The five filter types; one chip each, in the order they were set (R1, R9).</summary>
    private enum SearchChip { Type, Calendar, Dates, Attendee, Location }

    private const int SearchPageSize = 50;

    private readonly List<SearchChip> _chipOrder = [];
    private SearchKind _fKind = SearchKind.All;
    private string? _fCalendarKey;
    private string _fCalendarLabel = "";
    private IReadOnlyList<string> _fCalendarIds = [];
    private DateOnly? _fFrom;
    private DateOnly? _fTo;
    private string? _fAttendee;
    private string? _fLocation;
    private int _searchLimit = SearchPageSize;
    private bool _syncingFilters;
    private long _popupClosedAt;

    private readonly List<ToggleButton> _kindButtons = [];
    private readonly ComboBox _calendarBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly DatePicker _fromPicker = new() { Width = 150 };
    private readonly DatePicker _toPicker = new() { Width = 150 };
    private readonly TextBox _attendeeBox = new() { PlaceholderText = "Name or address" };
    private readonly ListBox _attendeeList = new()
    {
        MaxHeight = 160, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Visibility = Visibility.Collapsed,
    };
    private readonly TextBox _locationBox = new() { PlaceholderText = "Location contains" };

    private SearchFilter? CurrentSearchFilter()
    {
        var filter = new SearchFilter(
            _fKind,
            _fCalendarKey is null ? null : _fCalendarIds,
            _fFrom is { } from ? new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue)) : null,
            _fTo is { } to ? new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue)) : null,
            _fAttendee,
            _fLocation);
        return filter.IsEmpty ? null : filter;
    }

    private void SetupSearchFilters()
    {
        SearchPopup.Closed += (_, _) => _popupClosedAt = Environment.TickCount64;
        var itemStyle = (Style)FindResource("PopupListItem");
        _attendeeList.ItemContainerStyle = itemStyle;
        SearchFilterPanel.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                SearchPopup.IsOpen = false;
                SearchBox.Focus();
            }
        };

        var kinds = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var kind in Enum.GetValues<SearchKind>())
        {
            var button = new ToggleButton { Content = kind.ToString(), Tag = kind, Margin = new Thickness(0, 0, 4, 0) };
            button.Click += (_, _) =>
            {
                SetKind(kind);
                ApplySearchFilters();
            };
            _kindButtons.Add(button);
            kinds.Children.Add(button);
        }
        AddFilterRow("Type", kinds);

        AutomationProperties.SetName(_calendarBox, "Filter by calendar");
        _calendarBox.SelectionChanged += (_, _) =>
        {
            if (_syncingFilters || _calendarBox.SelectedItem is not ComboBoxItem { Tag: CalendarChoice choice })
            {
                return;
            }
            if (choice.Key is null)
            {
                ClearChip(SearchChip.Calendar);
            }
            else
            {
                _fCalendarKey = choice.Key;
                _fCalendarLabel = choice.Label;
                _fCalendarIds = choice.Ids;
                TouchChip(SearchChip.Calendar);
            }
            ApplySearchFilters();
        };
        AddFilterRow("Calendar", _calendarBox);

        AutomationProperties.SetName(_fromPicker, "Filter from date");
        AutomationProperties.SetName(_toPicker, "Filter to date");
        _fromPicker.SelectedDateChanged += (_, _) => OnDatesChanged();
        _toPicker.SelectedDateChanged += (_, _) => OnDatesChanged();
        var dates = new StackPanel { Orientation = Orientation.Horizontal };
        dates.Children.Add(_fromPicker);
        dates.Children.Add(new TextBlock { Text = "to", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        dates.Children.Add(_toPicker);
        AddFilterRow("Dates", dates);

        AutomationProperties.SetName(_attendeeBox, "Filter by attendee");
        AutomationProperties.SetName(_attendeeList, "Attendee suggestions");
        _attendeeBox.TextChanged += (_, _) => ShowAttendeeSuggestions();
        _attendeeBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _attendeeList.Items.OfType<ListBoxItem>().FirstOrDefault() is { } first)
            {
                e.Handled = true;
                ChooseAttendee((string)first.Tag);
            }
            else if (e.Key == Key.Down && _attendeeList.Items.OfType<ListBoxItem>().FirstOrDefault() is { } top)
            {
                e.Handled = true;
                _attendeeList.SelectedItem = top;
                top.Focus();
            }
        };
        _attendeeList.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _attendeeList.SelectedItem is ListBoxItem { Tag: string name })
            {
                e.Handled = true;
                ChooseAttendee(name);
            }
        };
        _attendeeList.PreviewMouseLeftButtonUp += (_, e) =>
        {
            for (var hit = e.OriginalSource as DependencyObject; hit is not null;
                hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit))
            {
                if (hit is ListBoxItem { Tag: string name })
                {
                    ChooseAttendee(name);
                    return;
                }
            }
        };
        var attendee = new StackPanel();
        attendee.Children.Add(_attendeeBox);
        attendee.Children.Add(_attendeeList);
        AddFilterRow("Attendee", attendee);

        AutomationProperties.SetName(_locationBox, "Filter by location");
        _locationBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                AddLocation();
            }
        };
        var add = new Button { Content = "Add", Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(add, "Add location filter");
        add.Click += (_, _) => AddLocation();
        var location = new DockPanel();
        DockPanel.SetDock(add, Dock.Right);
        location.Children.Add(add);
        location.Children.Add(_locationBox);
        AddFilterRow("Location", location);
    }

    private void AddFilterRow(string label, UIElement control)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(Caption(label));
        Grid.SetColumn((FrameworkElement)control, 1);
        row.Children.Add(control);
        SearchFilterPanel.Children.Add(row);
    }

    /// <summary>The Filters button (R1): opens the search popup if closed, and shows or hides the filters panel.</summary>
    private void OnSearchFiltersClick(object sender, RoutedEventArgs e)
    {
        // The press on this button closed the popup (StaysOpen=False) just before the click: with the panel showing,
        // the click means "hide the panel", so leave the popup closed rather than reopening it.
        if (!SearchPopup.IsOpen && SearchFilterPanel.Visibility == Visibility.Visible
            && Environment.TickCount64 - _popupClosedAt < 300)
        {
            SearchFilterPanel.Visibility = Visibility.Collapsed;
            return;
        }
        if (!SearchPopup.IsOpen)
        {
            SearchFilterPanel.Visibility = Visibility.Visible;
        }
        else
        {
            SearchFilterPanel.Visibility = SearchFilterPanel.IsVisible ? Visibility.Collapsed : Visibility.Visible;
        }
        if (SearchFilterPanel.IsVisible)
        {
            LoadCalendarChoices();
            SyncFilterControls();
        }
        _searchLimit = SearchPageSize;
        ShowSearchResults();
    }

    // ---- state ----

    private void SetKind(SearchKind kind)
    {
        _fKind = kind;
        if (kind == SearchKind.All)
        {
            _chipOrder.Remove(SearchChip.Type);
        }
        else
        {
            TouchChip(SearchChip.Type);
        }
    }

    private void OnDatesChanged()
    {
        if (_syncingFilters)
        {
            return;
        }
        _fFrom = _fromPicker.SelectedDate is { } from ? DateOnly.FromDateTime(from) : null;
        _fTo = _toPicker.SelectedDate is { } to ? DateOnly.FromDateTime(to) : null;
        if (_fFrom is null && _fTo is null)
        {
            _chipOrder.Remove(SearchChip.Dates);
        }
        else
        {
            TouchChip(SearchChip.Dates);
        }
        ApplySearchFilters();
    }

    private void ChooseAttendee(string name)
    {
        _fAttendee = name;
        TouchChip(SearchChip.Attendee);
        _syncingFilters = true;
        _attendeeBox.Text = "";
        _syncingFilters = false;
        _attendeeList.Items.Clear();
        _attendeeList.Visibility = Visibility.Collapsed;
        ApplySearchFilters();
        _attendeeBox.Focus();
    }

    private void AddLocation()
    {
        if (_locationBox.Text.Trim() is not { Length: > 0 } text)
        {
            return;
        }
        _fLocation = text;
        TouchChip(SearchChip.Location);
        _locationBox.Text = "";
        ApplySearchFilters();
        _locationBox.Focus();
    }

    /// <summary>One chip per type: setting a type again moves its chip to the end.</summary>
    private void TouchChip(SearchChip type)
    {
        _chipOrder.Remove(type);
        _chipOrder.Add(type);
    }

    private void ClearChip(SearchChip type)
    {
        _chipOrder.Remove(type);
        switch (type)
        {
            case SearchChip.Type:
                _fKind = SearchKind.All;
                break;
            case SearchChip.Calendar:
                _fCalendarKey = null;
                _fCalendarLabel = "";
                _fCalendarIds = [];
                break;
            case SearchChip.Dates:
                _fFrom = null;
                _fTo = null;
                break;
            case SearchChip.Attendee:
                _fAttendee = null;
                break;
            case SearchChip.Location:
                _fLocation = null;
                break;
        }
    }

    /// <summary>A chip's remove (click, Delete or Enter) and Backspace in the empty box (R1, R9).</summary>
    private void RemoveChip(SearchChip type)
    {
        ClearChip(type);
        ApplySearchFilters();
        SearchBox.Focus();
    }

    /// <summary>Clear filters (ui-states R4).</summary>
    private void ClearAllFilters()
    {
        foreach (var type in _chipOrder.ToList())
        {
            ClearChip(type);
        }
        if (_typedDate is not null)
        {
            _dismissedDateText = SearchBox.Text; // search-date-chips R6: Clear filters also removes the typed chip
        }
        ApplySearchFilters();
        SearchBox.Focus();
    }

    /// <summary>A changed filter re-runs the search at once (R2); the result limit starts again at 50.</summary>
    private void ApplySearchFilters()
    {
        _searchLimit = SearchPageSize;
        SyncFilterControls();
        RebuildChips();
        ShowSearchResults();
    }

    // ---- controls ----

    /// <summary>Makes the panel controls show the state, e.g. after a chip was removed.</summary>
    private void SyncFilterControls()
    {
        _syncingFilters = true;
        foreach (var button in _kindButtons)
        {
            button.IsChecked = (SearchKind)button.Tag == _fKind;
        }
        _fromPicker.SelectedDate = _fFrom?.ToDateTime(TimeOnly.MinValue);
        _toPicker.SelectedDate = _fTo?.ToDateTime(TimeOnly.MinValue);
        _calendarBox.SelectedItem = _calendarBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => ((CalendarChoice)i.Tag).Key == _fCalendarKey)
            ?? _calendarBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _syncingFilters = false;
    }

    private sealed record CalendarChoice(string? Key, string Label, IReadOnlyList<string> Ids);

    /// <summary>Any calendar, then per account "(all)" and its visible calendars and task lists (R2).</summary>
    private void LoadCalendarChoices()
    {
        if (Store is not { } store)
        {
            return;
        }
        _syncingFilters = true;
        _calendarBox.Items.Clear();
        _calendarBox.Items.Add(new ComboBoxItem { Content = "Any calendar", Tag = new CalendarChoice(null, "", []) });
        var hidden = store.GetHiddenCollectionIds();
        foreach (var account in store.GetAccounts())
        {
            var visible = _collections.Values.Where(c => c.AccountId == account.Id && !hidden.Contains(c.Id)).ToList();
            if (visible.Count == 0)
            {
                continue;
            }
            _calendarBox.Items.Add(new ComboBoxItem
            {
                Content = $"{account.DisplayName} (all)",
                Tag = new CalendarChoice("A:" + account.Id, account.DisplayName, visible.Select(c => c.Id).ToList()),
            });
            foreach (var collection in visible)
            {
                _calendarBox.Items.Add(new ComboBoxItem
                {
                    Content = "    " + collection.Name,
                    Tag = new CalendarChoice("C:" + collection.Id, collection.Name, [collection.Id]),
                });
            }
        }
        _syncingFilters = false;
    }

    private void ShowAttendeeSuggestions()
    {
        if (_syncingFilters)
        {
            return;
        }
        _attendeeList.Items.Clear();
        var text = _attendeeBox.Text.Trim();
        if (text.Length > 0 && Store is { } store)
        {
            foreach (var name in store.People(text, 8))
            {
                _attendeeList.Items.Add(new ListBoxItem { Content = name, Tag = name });
            }
        }
        _attendeeList.Visibility = _attendeeList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- chips ----

    private void RebuildChips()
    {
        SearchChips.Children.Clear();
        var any = _chipOrder.Count > 0 || _typedDate is not null;
        if (TypedDateChip() is { } typedChip)
        {
            SearchChips.Children.Add(typedChip);
        }
        foreach (var type in _chipOrder)
        {
            SearchChips.Children.Add(ChipFor(type));
        }
        if (any)
        {
            var clear = new Wpf.Ui.Controls.Button { Content = "Clear filters", Appearance = ControlAppearance.Transparent, Margin = new Thickness(0, 0, 0, 4) };
            AutomationProperties.SetName(clear, "Clear filters");
            clear.Click += (_, _) => ClearAllFilters();
            SearchChips.Children.Add(clear);
        }
        SearchChips.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A focusable chip styled like the quick-add chips; click, Delete or Enter removes it (R1, R9).</summary>
    private FrameworkElement ChipFor(SearchChip type)
    {
        var text = ChipText(type);
        return MakeChip(text, "Remove filter " + text, () => RemoveChip(type));
    }

    /// <summary>The chip look shared by hand-set chips and the typed date chip (search-date-chips R3).</summary>
    private FrameworkElement MakeChip(string text, string automationName, Action remove)
    {
        var label = new TextBlock
        {
            Text = text + "  ✕",
            Style = Theme.Style("CaptionTextBlockStyle"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 340,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var chip = new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 4, 4),
            Focusable = true,
            Cursor = Cursors.Hand,
            ToolTip = "Remove filter",
        };
        chip.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");
        chip.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        AutomationProperties.SetName(chip, automationName);
        chip.GotKeyboardFocus += (_, _) => chip.SetResourceReference(Border.BorderBrushProperty, "TextPrimaryBrush");
        chip.LostKeyboardFocus += (_, _) => chip.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            remove();
        };
        chip.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Delete or Key.Enter)
            {
                e.Handled = true;
                remove();
            }
        };
        return chip;
    }

    private string ChipText(SearchChip type)
    {
        switch (type)
        {
            case SearchChip.Type:
                return _fKind.ToString();
            case SearchChip.Calendar:
                return _fCalendarLabel;
            case SearchChip.Attendee:
                return $"With {_fAttendee}";
            case SearchChip.Location:
                return $"At {_fLocation}";
            default:
                return DatesText();
        }
    }

    /// <summary>"12 Nov – 19 Nov 2026", "From 12 Nov" or "Until 19 Nov"; R7 adds the MS365 sync start.</summary>
    private string DatesText()
    {
        var culture = CultureInfo.CurrentCulture;
        string Day(DateOnly d, bool year) =>
            d.ToDateTime(TimeOnly.MinValue).ToString(DateFormats.MonthDay(culture) + (year ? " yyyy" : ""), culture);
        string text;
        if (_fFrom is { } from && _fTo is { } to)
        {
            text = $"{Day(from, from.Year != to.Year)} – {Day(to, true)}";
        }
        else if (_fFrom is { } only)
        {
            text = "From " + Day(only, only.Year != _today.Year);
        }
        else
        {
            var until = _fTo!.Value;
            text = "Until " + Day(until, until.Year != _today.Year);
        }
        if (_graphAccounts.Count > 0 && _fFrom is { } start && start < GraphSync.WindowStart(_today))
        {
            text += $" · searched from {GraphSync.WindowStart(_today).ToString("d MMM yyyy", culture)}";
        }
        return text;
    }
}
