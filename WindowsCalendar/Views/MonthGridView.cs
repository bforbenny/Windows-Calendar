using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Views;

/// <summary>
/// Month view (calendar-views R1a, R3; display-scaling R6): a continuous column of weeks, about five filling the
/// height, scrolling vertically. Events are chips or multi-day bars in rows per week, as many as fit, then
/// "+n more", which opens that day in Agenda. Only weeks near the viewport are built.
/// </summary>
internal sealed partial class MonthGridView : UserControl, IScrollingView, IDragHost
{
    private const int VisibleWeeks = 5;
    private const int Buffer = 2;
    private const double DayNumberHeight = 28;
    private const double ChipHeight = 20;
    private const double ChipSpacing = 2;

    private readonly Grid _weekdays = new() { Height = 24 };
    private readonly ScrollViewer _scroll = new()
    {
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        ZoomMode = ZoomMode.Disabled,
    };
    private readonly Canvas _canvas = new();
    // Week numbers left of the rows, scrolled with them (calendar-shading R7 interim).
    private const double WeekColumnWidth = 36;
    private readonly ScrollViewer _weekStrip = ViewParts.FollowerStrip(horizontal: false);
    private readonly Canvas _weekCanvas = new() { Width = WeekColumnWidth };
    private readonly ColumnDefinition _weekColumn = new() { Width = new GridLength(0) };
    private readonly Dictionary<int, UIElement> _weeks = [];

    private DateOnly _first;
    private int _weekCount;
    private IReadOnlyList<DisplayEvent> _events = [];
    private IReadOnlyDictionary<DateOnly, string> _holidays = new Dictionary<DateOnly, string>();
    private double _rowHeight;
    private double _width;
    private int _capacity;
    private DateOnly? _anchor;
    private int _grabIndex;
    private bool _dragging;

    public MonthGridView()
    {
        for (var c = 0; c < 7; c++)
        {
            _weekdays.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(_weekColumn);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _scroll.Content = _canvas;
        _weekStrip.Content = _weekCanvas;
        Grid.SetRow(_scroll, 1);
        Grid.SetColumn(_scroll, 1);
        Grid.SetColumn(_weekdays, 1);
        Grid.SetRow(_weekStrip, 1);
        root.Children.Add(_weekdays);
        root.Children.Add(_weekStrip);
        root.Children.Add(_scroll);
        Overlay = ViewParts.Overlay(root, 2, 2);
        _scroll.ViewChanging += (_, e) => _weekStrip.ChangeView(null, e.NextView.VerticalOffset, null, true);
        Content = root;

        _scroll.ViewChanged += (_, _) =>
        {
            if (_rowHeight > 0)
            {
                _anchor = _first.AddDays(7 * (int)Math.Round(_scroll.VerticalOffset / _rowHeight));
            }
            Realize();
        };
        _scroll.SizeChanged += (_, _) => Relayout();
    }

    /// <summary>Raised when "+n more" is clicked.</summary>
    public event Action<DateOnly>? DayRequested;

    public event Action<DateOnly>? VisibleDayChanged;

    public event Action<DisplayEvent>? EventOpened;

    public event Action<DateTime, bool>? CreateRequested;

    public event Action<DisplayEvent, DragTarget>? EventDropped;

    public Canvas Overlay { get; }

    public void SetData(DateOnly first, int days, IReadOnlyList<DisplayEvent> events, IReadOnlyDictionary<DateOnly, string> holidays)
    {
        (_first, _weekCount, _events, _holidays) = (first, days / 7, events, holidays);
        _weekColumn.Width = new GridLength(ViewParts.ShowWeekNumbers ? WeekColumnWidth : 0);
        _weekdays.Children.Clear();
        for (var c = 0; c < 7; c++)
        {
            var name = new TextBlock
            {
                Text = first.AddDays(c).ToString("ddd"),
                Style = Theme.Style("CaptionTextBlockStyle"),
                Foreground = Theme.Brush("TextSecondaryBrush"),
                Margin = new Thickness(8, 4, 0, 0),
            };
            Grid.SetColumn(name, c);
            _weekdays.Children.Add(name);
        }
        Relayout(force: true);
    }

    public void ScrollTo(DateOnly day, bool toNow)
    {
        _anchor = ViewPeriod.StartOfWeek(day, _first.DayOfWeek);
        ApplyAnchor();
    }

    public bool UpdateNowLine() => true;

    public void Reveal(DateOnly day, DateTime? start) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_rowHeight <= 0)
        {
            return;
        }
        var top = (day.DayNumber - _first.DayNumber) / 7 * _rowHeight;
        if (top < _scroll.VerticalOffset)
        {
            _scroll.ChangeView(null, top, null, true);
        }
        else if (top + _rowHeight > _scroll.VerticalOffset + _scroll.ViewportHeight)
        {
            _scroll.ChangeView(null, top + _rowHeight - _scroll.ViewportHeight, null, true);
        }
    });

    public void Page(int direction) =>
        _scroll.ChangeView(null, Math.Max(0, _scroll.VerticalOffset + direction * VisibleWeeks * _rowHeight), null);

    private void Relayout(bool force = false)
    {
        if (_scroll.ActualHeight <= 0)
        {
            return;
        }
        var rowHeight = Math.Max(100, _scroll.ActualHeight / VisibleWeeks);
        var width = _scroll.ActualWidth;
        if (!force && Math.Abs(rowHeight - _rowHeight) < 0.5 && Math.Abs(width - _width) < 0.5)
        {
            return;
        }
        (_rowHeight, _width) = (rowHeight, width);
        _capacity = Math.Max(1, (int)((_rowHeight - DayNumberHeight) / (ChipHeight + ChipSpacing)));
        foreach (var week in _weeks.Values)
        {
            _canvas.Children.Remove(week);
        }
        _weeks.Clear();
        _canvas.Width = _width;
        _canvas.Height = _weekCanvas.Height = _weekCount * _rowHeight;
        Realize();
        ApplyAnchor();
    }

    private void ApplyAnchor()
    {
        if (_anchor is not { } day || _rowHeight <= 0)
        {
            return;
        }
        var y = (day.DayNumber - _first.DayNumber) / 7 * _rowHeight;
        DispatcherQueue.TryEnqueue(() => _scroll.ChangeView(null, y, null, true));
    }

    private void Realize()
    {
        if (_rowHeight <= 0)
        {
            return;
        }
        var (a, b) = ViewPeriod.Realized(_scroll.VerticalOffset, _scroll.ViewportHeight, _rowHeight, _weekCount, Buffer);
        foreach (var index in _weeks.Keys.Where(i => !_dragging && (i < a || i > b)).ToList())
        {
            _canvas.Children.Remove(_weeks[index]);
            _weeks.Remove(index);
        }
        for (var w = a; w <= b; w++)
        {
            if (!_weeks.ContainsKey(w))
            {
                var week = BuildWeek(_first.AddDays(w * 7));
                ViewParts.Place(week, 0, w * _rowHeight);
                _canvas.Children.Add(week);
                _weeks[w] = week;
            }
        }

        _weekCanvas.Children.Clear();
        if (ViewParts.ShowWeekNumbers)
        {
            for (var w = a; w <= b; w++)
            {
                var number = new TextBlock
                {
                    Text = ViewPeriod.WeekNumber(_first.AddDays(w * 7), _first.DayOfWeek).ToString(),
                    Style = Theme.Style("CaptionTextBlockStyle"),
                    Foreground = Theme.Brush("TextSecondaryBrush"),
                    Width = WeekColumnWidth,
                    TextAlignment = TextAlignment.Center,
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(number, ViewParts.WeekLabel(_first.AddDays(w * 7), _first.DayOfWeek));
                ViewParts.Place(number, 0, w * _rowHeight + 8);
                _weekCanvas.Children.Add(number);
            }
        }

        // The title follows the week in the middle of the viewport.
        var middleWeek = (int)((_scroll.VerticalOffset + _scroll.ViewportHeight / 2) / _rowHeight);
        VisibleDayChanged?.Invoke(_first.AddDays(middleWeek * 7 + 3));
    }

    private Grid BuildWeek(DateOnly weekStart)
    {
        var row = new Grid { Width = _width, Height = _rowHeight };
        for (var c = 0; c < 7; c++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var today = DateOnly.FromDateTime(DateTime.Now);
        for (var c = 0; c < 7; c++)
        {
            AddDayCell(row, weekStart.AddDays(c), c, weekStart.AddDays(c) == today);
        }
        AddWeekEvents(row, weekStart);
        return row;
    }

    private void AddDayCell(Grid row, DateOnly date, int column, bool isToday)
    {
        var cell = new Border
        {
            BorderBrush = Theme.Brush("DividerBrush"),
            BorderThickness = new Thickness(column == 0 ? 0 : 1, 1, 0, 0),
            Background = ViewParts.DayShade(date, isToday, _holidays.ContainsKey(date)) ?? ViewParts.Clickable,
        };
        // A click on the day creates an all-day event (R5 interim).
        cell.Tapped += (_, _) => CreateRequested?.Invoke(date.ToDateTime(TimeOnly.MinValue), true);
        var number = new TextBlock
        {
            // The first of each month carries the month name, since weeks run on without month breaks.
            Text = date.Day == 1 ? date.ToString("MMM d") : date.Day.ToString(),
            Style = Theme.Style(isToday || date.Day == 1 ? "BodyStrongTextBlockStyle" : "BodyTextBlockStyle"),
            Foreground = isToday ? Theme.Brush("TodayChipTextBrush") : Theme.Brush("TextPrimaryBrush"),
        };
        var chip = new Border
        {
            Background = isToday ? Theme.Brush("TodayChipBrush") : null,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(4, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = number,
        };
        var holiday = _holidays.GetValueOrDefault(date);
        if (holiday is null)
        {
            cell.Child = chip;
        }
        else
        {
            // The holiday name sits beside the date (calendar-shading R4), trimmed to the cell.
            var top = new Grid { VerticalAlignment = VerticalAlignment.Top };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var name = ViewParts.Caption(holiday);
            name.Margin = new Thickness(0, 6, 0, 0);
            Grid.SetColumn(name, 1);
            top.Children.Add(chip);
            top.Children.Add(name);
            cell.Child = top;
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, holiday is null ? date.ToString("D") : $"{holiday}, {date:D}");
        Grid.SetColumn(cell, column);
        row.Children.Add(cell);
    }

    // ---- Drag (R7): to another day, keeping the time and length. ----

    private int DayIndexAt(Windows.Foundation.Point canvas)
    {
        var row = Math.Clamp((int)Math.Floor(canvas.Y / _rowHeight), 0, _weekCount - 1);
        var column = Math.Clamp((int)Math.Floor(canvas.X / (_width / 7)), 0, 6);
        return row * 7 + column;
    }

    public void BeginDrag(DisplayEvent e, DragMode mode, Windows.Foundation.Point window)
    {
        _dragging = true;
        _grabIndex = DayIndexAt(ViewParts.To(_canvas, window));
    }

    public DragTarget? Target(DisplayEvent e, DragMode mode, Windows.Foundation.Point window)
    {
        if (_rowHeight <= 0)
        {
            return null;
        }
        var (s, en) = DragMath.MoveDays(e.Start, e.End, DayIndexAt(ViewParts.To(_canvas, window)) - _grabIndex);
        var moved = e with { Start = s, End = en };
        var index = moved.FirstDay.DayNumber - _first.DayNumber;
        var (row, column) = (index / 7, index % 7);
        var span = Math.Min(moved.LastDay.DayNumber - moved.FirstDay.DayNumber + 1, 7 - column);
        var cell = _width / 7;
        var rect = new Windows.Foundation.Rect(column * cell + 1, row * _rowHeight + DayNumberHeight, span * cell - 2, ChipHeight);
        return new DragTarget(s, en, e.AllDay, ViewParts.InOverlay(_canvas, Overlay, rect));
    }

    public void AutoScroll(Windows.Foundation.Point window)
    {
        var dy = ViewParts.EdgeStep(ViewParts.To(_scroll, window).Y, _scroll.ActualHeight);
        if (dy != 0)
        {
            _scroll.ChangeView(null, _scroll.VerticalOffset + dy, null, true);
        }
    }

    public void EndDrag()
    {
        _dragging = false;
        Realize();
    }

    public void CompleteDrag(DisplayEvent e, DragTarget target) => EventDropped?.Invoke(e, target);

    public void OpenEvent(DisplayEvent e) => EventOpened?.Invoke(e);

    private void AddWeekEvents(Grid row, DateOnly weekStart)
    {
        var weekEnd = weekStart.AddDays(6);
        var inWeek = _events.Where(e => e.FirstDay <= weekEnd && e.LastDay >= weekStart).ToList();
        var spans = inWeek.Select(e => (
            Math.Max(0, e.FirstDay.DayNumber - weekStart.DayNumber),
            Math.Min(6, e.LastDay.DayNumber - weekStart.DayNumber))).ToList();
        var lanes = EventLayout.Lanes(spans);

        // A day overflows when it needs more rows than fit; then its last row shows "+n more".
        var rowsNeeded = new int[7];
        for (var i = 0; i < inWeek.Count; i++)
        {
            for (var d = spans[i].Item1; d <= spans[i].Item2; d++)
            {
                rowsNeeded[d] = Math.Max(rowsNeeded[d], lanes[i] + 1);
            }
        }
        bool Overflows(int d) => rowsNeeded[d] > _capacity;
        bool Visible(int i) => lanes[i] < _capacity - 1
            || lanes[i] == _capacity - 1 && Enumerable.Range(spans[i].Item1, spans[i].Item2 - spans[i].Item1 + 1).All(d => !Overflows(d));

        var layer = new Grid { Margin = new Thickness(0, DayNumberHeight, 0, 0), RowSpacing = ChipSpacing };
        for (var c = 0; c < 7; c++)
        {
            layer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (var r = 0; r < _capacity; r++)
        {
            layer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ChipHeight) });
        }

        var hidden = new int[7];
        for (var i = 0; i < inWeek.Count; i++)
        {
            if (!Visible(i))
            {
                for (var d = spans[i].Item1; d <= spans[i].Item2; d++)
                {
                    hidden[d]++;
                }
                continue;
            }
            var e = inWeek[i];
            var card = EventCard.Create(e, Theme.Calendar(e.Collection.Colour), ChipHeight, chip: true,
                prefix: e.InAllDayLane ? null : e.Start.ToString("t"));
            EventDrag.Attach(card, e, this);
            Grid.SetRow(card, lanes[i]);
            Grid.SetColumn(card, spans[i].Item1);
            Grid.SetColumnSpan(card, spans[i].Item2 - spans[i].Item1 + 1);
            layer.Children.Add(card);
        }

        for (var d = 0; d < 7; d++)
        {
            if (hidden[d] == 0)
            {
                continue;
            }
            var date = weekStart.AddDays(d);
            var more = new HyperlinkButton
            {
                Content = new TextBlock { Text = $"+{hidden[d]} more", Style = Theme.Style("CaptionTextBlockStyle") },
                Padding = new Thickness(4, 0, 4, 0),
                MinHeight = 0,
            };
            more.Click += (_, _) => DayRequested?.Invoke(date);
            Grid.SetRow(more, _capacity - 1);
            Grid.SetColumn(more, d);
            layer.Children.Add(more);
        }

        Grid.SetColumnSpan(layer, 7);
        row.Children.Add(layer);
    }
}
