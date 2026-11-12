using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Views;

/// <summary>
/// Month view (calendar-views R1a, R3; display-scaling R6): a continuous column of weeks, about five filling the
/// height, scrolling vertically. Events are chips or multi-day bars in rows per week, as many as fit, then
/// "+n more", which opens that day in Agenda. The weeks are fixed-height rows in a <see cref="VirtualStrip"/>.
/// </summary>
internal sealed class MonthGridView : UserControl, IScrollingView, IDragHost
{
    private const int VisibleWeeks = 5;
    // Rows holding text grow with Windows Text size (display-scaling R8).
    private static double DayNumberHeight => TextScale.Of(28);
    private static double ChipHeight => TextScale.Of(20);
    private const double ChipSpacing = 2;
    // Week numbers left of the rows, inside each row (calendar-shading R7 interim).
    private const double WeekColumnWidth = 36;

    private readonly Grid _weekdays = new();
    private readonly ColumnDefinition _weekColumn = new() { Width = new GridLength(0) };
    private readonly VirtualStrip _strip;

    private DateOnly _first;
    private int _weekCount;
    private IReadOnlyList<DisplayEvent> _events = [];
    private IReadOnlyDictionary<DateOnly, string> _holidays = new Dictionary<DateOnly, string>();
    private double _rowHeight;
    private double _width;
    private int _capacity;
    private DateOnly? _anchor;
    private int _grabIndex;

    public MonthGridView()
    {
        for (var c = 0; c < 7; c++)
        {
            _weekdays.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        _strip = new VirtualStrip(Orientation.Vertical, scrollBars: true, () => new WeekRow(this), (row, index) => ((WeekRow)row).Bind(index));

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(_weekColumn);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_weekdays, 1);
        Grid.SetRow(_strip, 1);
        Grid.SetColumnSpan(_strip, 2);
        root.Children.Add(_weekdays);
        root.Children.Add(_strip);
        Overlay = ViewParts.Overlay(root, 2, 2);
        Content = root;

        Scroll.ScrollChanged += (_, e) =>
        {
            if (e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)
            {
                Relayout();
            }
            if (_rowHeight > 0)
            {
                _anchor = _first.AddDays(7 * (int)Math.Round(Scroll.VerticalOffset / _rowHeight));
                // The title follows the week in the middle of the viewport.
                var middleWeek = (int)((Scroll.VerticalOffset + Scroll.ViewportHeight / 2) / _rowHeight);
                VisibleDayChanged?.Invoke(_first.AddDays(middleWeek * 7 + 3));
            }
        };
    }

    /// <summary>Raised when "+n more" is clicked.</summary>
    public event Action<DateOnly>? DayRequested;

    public event Action<DateOnly>? VisibleDayChanged;

    public event Action<DisplayEvent>? EventOpened;

    public event Action<DateTime, bool>? CreateRequested;

    public event Action<DisplayEvent, DragTarget>? EventDropped;

    public Canvas Overlay { get; }

    private ScrollViewer Scroll => _strip.Scroll;

    private double WeekColumn => ViewParts.ShowWeekNumbers ? WeekColumnWidth : 0;

    public void SetData(DateOnly first, int days, IReadOnlyList<DisplayEvent> events, IReadOnlyDictionary<DateOnly, string> holidays)
    {
        (_first, _weekCount, _events, _holidays) = (first, days / 7, events, holidays);
        _weekColumn.Width = new GridLength(WeekColumn);
        _weekdays.Height = TextScale.Of(24);
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

    public void Reveal(DateOnly day, DateTime? start) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        if (_rowHeight <= 0)
        {
            return;
        }
        var top = (day.DayNumber - _first.DayNumber) / 7 * _rowHeight;
        if (top < Scroll.VerticalOffset)
        {
            Scroll.ScrollToVerticalOffset(top);
        }
        else if (top + _rowHeight > Scroll.VerticalOffset + Scroll.ViewportHeight)
        {
            Scroll.ScrollToVerticalOffset(top + _rowHeight - Scroll.ViewportHeight);
        }
    });

    public void Page(int direction) =>
        Scroll.ScrollToVerticalOffset(Math.Max(0, Scroll.VerticalOffset + direction * VisibleWeeks * _rowHeight));

    /// <summary>Row height from the viewport; the rows are rebound with the new size, capacity and data.</summary>
    private void Relayout(bool force = false)
    {
        var height = Scroll.ViewportHeight > 0 ? Scroll.ViewportHeight : _strip.ActualHeight;
        if (height <= 0)
        {
            return;
        }
        var rowHeight = ViewParts.WholePixels(Math.Max(100, height / VisibleWeeks), this);
        var width = ViewParts.ItemsWidth(Scroll);
        var resized = Math.Abs(rowHeight - _rowHeight) >= 0.5;
        if (!force && !resized && Math.Abs(width - _width) < 0.5)
        {
            return;
        }
        _capacity = Math.Max(1, (int)((rowHeight - DayNumberHeight) / (ChipHeight + ChipSpacing)));
        (_rowHeight, _width) = (rowHeight, width);
        if (resized)
        {
            // New row height: the strip starts over, so offsets map to weeks by the new height.
            Serilog.Log.Information("Month view row height {Height:F1}", rowHeight);
            _strip.Reset(_weekCount);
        }
        else
        {
            _strip.SetCount(_weekCount);
        }
        ApplyAnchor();
    }

    private void ApplyAnchor()
    {
        if (_anchor is not { } day || _rowHeight <= 0)
        {
            return;
        }
        var y = (day.DayNumber - _first.DayNumber) / 7 * _rowHeight;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Scroll.ScrollToVerticalOffset(y));
    }

    // ---- Drag (R7): to another day, keeping the time and length. Positions are in the whole column of weeks. ----

    private double CellWidth => (_width - WeekColumn) / 7;

    private Point InWeeks(Point window)
    {
        var p = ViewParts.To(Scroll, window);
        return new Point(p.X + Scroll.HorizontalOffset, p.Y + Scroll.VerticalOffset);
    }

    private int DayIndexAt(Point point)
    {
        var row = Math.Clamp((int)Math.Floor(point.Y / _rowHeight), 0, _weekCount - 1);
        var column = Math.Clamp((int)Math.Floor((point.X - WeekColumn) / CellWidth), 0, 6);
        return row * 7 + column;
    }

    public void BeginDrag(DisplayEvent e, DragMode mode, Point window)
    {
        _strip.KeepAll = true;
        _grabIndex = DayIndexAt(InWeeks(window));
    }

    public DragTarget? Target(DisplayEvent e, DragMode mode, Point window)
    {
        if (_rowHeight <= 0)
        {
            return null;
        }
        var (s, en) = DragMath.MoveDays(e.Start, e.End, DayIndexAt(InWeeks(window)) - _grabIndex);
        var moved = e with { Start = s, End = en };
        var index = moved.FirstDay.DayNumber - _first.DayNumber;
        var (row, column) = (index / 7, index % 7);
        var span = Math.Min(moved.LastDay.DayNumber - moved.FirstDay.DayNumber + 1, 7 - column);
        var rect = new Rect(WeekColumn + column * CellWidth + 1 - Scroll.HorizontalOffset,
            row * _rowHeight + DayNumberHeight - Scroll.VerticalOffset, span * CellWidth - 2, ChipHeight);
        return new DragTarget(s, en, e.AllDay, ViewParts.InOverlay(Scroll, Overlay, rect));
    }

    public void AutoScroll(Point window)
    {
        var dy = ViewParts.EdgeStep(ViewParts.To(Scroll, window).Y, Scroll.ViewportHeight);
        if (dy != 0)
        {
            Scroll.ScrollToVerticalOffset(Scroll.VerticalOffset + dy);
        }
    }

    public void EndDrag() => _strip.KeepAll = false;

    public void CompleteDrag(DisplayEvent e, DragTarget target) => EventDropped?.Invoke(e, target);

    public void OpenEvent(DisplayEvent e) => EventOpened?.Invoke(e);

    /// <summary>One week: the week number, seven day cells (kept across reuse) and a layer of event chips (rebuilt).</summary>
    private sealed class WeekRow : Grid
    {
        private readonly MonthGridView _view;
        private readonly TextBlock _weekNumber;
        private readonly DayCell[] _cells = new DayCell[7];
        private Grid? _layer;

        public WeekRow(MonthGridView view)
        {
            _view = view;
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (var c = 0; c < 7; c++)
            {
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            _weekNumber = new TextBlock
            {
                Style = Theme.Style("CaptionTextBlockStyle"),
                Width = WeekColumnWidth,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            Children.Add(_weekNumber);
            for (var c = 0; c < 7; c++)
            {
                _cells[c] = new DayCell(view, c);
                SetColumn(_cells[c].Root, c + 1);
                Children.Add(_cells[c].Root);
            }
        }

        public void Bind(int index)
        {
            Height = _view._rowHeight;
            var weekStart = _view._first.AddDays(index * 7);
            _weekNumber.Visibility = ViewParts.ShowWeekNumbers ? Visibility.Visible : Visibility.Collapsed;
            if (ViewParts.ShowWeekNumbers)
            {
                _weekNumber.Foreground = Theme.Brush("TextSecondaryBrush");
                _weekNumber.Text = ViewPeriod.WeekNumber(weekStart, _view._first.DayOfWeek).ToString();
                AutomationProperties.SetName(_weekNumber, ViewParts.WeekLabel(weekStart, _view._first.DayOfWeek));
            }
            var today = DateOnly.FromDateTime(DateTime.Now);
            for (var c = 0; c < 7; c++)
            {
                var date = weekStart.AddDays(c);
                _cells[c].Bind(date, date == today, _view._holidays.GetValueOrDefault(date));
            }
            if (_layer is not null)
            {
                Children.Remove(_layer);
            }
            _layer = _view.BuildEventLayer(weekStart);
            SetColumn(_layer, 1);
            SetColumnSpan(_layer, 7);
            Children.Add(_layer);
        }
    }

    /// <summary>A day cell: border and shade, the date (in a chip for today) and an optional holiday name.</summary>
    private sealed class DayCell
    {
        private readonly TextBlock _number = new();
        private readonly Border _chip;
        private readonly Grid _top = new() { VerticalAlignment = VerticalAlignment.Top };
        private TextBlock? _holiday;
        private FrameworkElement? _mark;
        private DateOnly _date;

        public DayCell(MonthGridView view, int column)
        {
            _chip = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(4, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = _number,
            };
            _top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _top.Children.Add(_chip);
            Root = new Border
            {
                BorderThickness = new Thickness(column == 0 ? 0 : 1, 1, 0, 0),
                Child = _top,
            };
            // A click on the day creates an all-day event (R5 interim).
            Root.MouseLeftButtonUp += (_, _) => view.CreateRequested?.Invoke(_date.ToDateTime(TimeOnly.MinValue), true);
        }

        public Border Root { get; }

        public void Bind(DateOnly date, bool isToday, string? holiday)
        {
            _date = date;
            Root.BorderBrush = Theme.Brush("DividerBrush");
            Root.Background = ViewParts.DayShade(date, isToday, holiday is not null) ?? ViewParts.Clickable;
            // The first of each month carries the month name, since weeks run on without month breaks.
            _number.Text = date.Day == 1 ? date.ToString(DateFormats.MonthDay(System.Globalization.CultureInfo.CurrentCulture)) : date.Day.ToString();
            _number.Style = Theme.Style(isToday || date.Day == 1 ? "BodyStrongTextBlockStyle" : "BodyTextBlockStyle");
            _number.Foreground = isToday ? Theme.Brush("TodayChipTextBrush") : Theme.Brush("TextPrimaryBrush");
            _chip.Background = isToday ? Theme.Brush("TodayChipBrush") : null;
            if (_holiday is not null)
            {
                _top.Children.Remove(_holiday);
                _holiday = null;
            }
            // The clash and short-gap icon beside the date (schedule-gap-warning R11).
            if (_mark is not null)
            {
                _top.Children.Remove(_mark);
            }
            _mark = ProblemMarks.DayIcon(date);
            if (_mark is not null)
            {
                _mark.Margin = new Thickness(4, 6, 0, 0);
                _mark.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(_mark, 1);
                _top.Children.Add(_mark);
            }
            if (holiday is not null)
            {
                // The holiday name sits beside the date (calendar-shading R4), trimmed to the cell.
                _holiday = ViewParts.Caption(holiday);
                _holiday.Margin = new Thickness(0, 6, 0, 0);
                Grid.SetColumn(_holiday, 2);
                _top.Children.Add(_holiday);
            }
            AutomationProperties.SetName(Root, holiday is null ? date.ToString("D") : $"{holiday}, {date:D}");
        }
    }

    private Grid BuildEventLayer(DateOnly weekStart)
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

        var layer = new Grid { Margin = new Thickness(0, DayNumberHeight, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        for (var c = 0; c < 7; c++)
        {
            layer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (var r = 0; r < _capacity; r++)
        {
            layer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ChipHeight + ChipSpacing) });
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
            card.VerticalAlignment = VerticalAlignment.Top;
            ProblemMarks.Decorate(card, e.Key);
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
            // The whole row across the cell is clickable, a larger hit area than the link (ui-style R5); the link keeps
            // keyboard access.
            var more = new TextBlock
            {
                Style = Theme.Style("CaptionTextBlockStyle"),
                Padding = new Thickness(4, 2, 4, 0),
                Background = ViewParts.Clickable,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run($"+{hidden[d]} more"));
            link.Click += (_, _) => DayRequested?.Invoke(date);
            more.Inlines.Add(link);
            more.MouseLeftButtonDown += (_, args) => args.Handled = true;
            more.MouseLeftButtonUp += (_, args) =>
            {
                // A press on the link text is the link's own click.
                if (args.OriginalSource is not System.Windows.Documents.TextElement)
                {
                    args.Handled = true;
                    DayRequested?.Invoke(date);
                }
            };
            Grid.SetRow(more, _capacity - 1);
            Grid.SetColumn(more, d);
            layer.Children.Add(more);
        }
        return layer;
    }
}
