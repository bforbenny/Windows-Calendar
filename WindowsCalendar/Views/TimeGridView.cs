using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Views;

/// <summary>
/// Week view (calendar-views R1a, R3, R4, R4a): a continuous strip of day columns, seven filling the width, that
/// scrolls horizontally through days and vertically through hours. Day headers and the all-day lane follow the
/// horizontal scroll; hour labels follow the vertical scroll, in the same layout pass. The day columns and headers
/// are fixed-width items in <see cref="VirtualStrip"/>s.
/// </summary>
internal sealed class TimeGridView : UserControl, IScrollingView, IDragHost
{
    private const double GutterWidth = 56;
    private const double LaneHeight = 22;
    private const int VisibleDays = 7;
    private static readonly double DayHeight = DayColumnPanel.PixelsFor(24 * 60);

    private readonly VirtualStrip _body;
    private readonly VirtualStrip _headers;
    private readonly ScrollViewer _laneScroll = ViewParts.FollowerStrip(horizontal: true);
    private readonly ScrollViewer _gutterScroll = ViewParts.FollowerStrip(horizontal: false);
    private readonly Canvas _laneCanvas = new() { Background = ViewParts.Clickable };
    // "Wk 45" above the hour labels, for the week in the middle of the view (calendar-shading R7 interim).
    private readonly TextBlock _weekLabel = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Dictionary<int, UIElement> _laneCards = [];

    private IReadOnlyDictionary<DateOnly, string> _holidays = new Dictionary<DateOnly, string>();
    private DateOnly _first;
    private int _count;
    private Dictionary<DateOnly, List<DisplayEvent>> _timed = [];
    private List<DisplayEvent> _laneEvents = [];
    private (int First, int Last)[] _laneSpans = [];
    private int[] _laneRows = [];
    private double _columnWidth;
    private DateOnly? _anchor;
    private bool _anchorToNow;
    private Rectangle? _nowLine;
    private DateTime _grabTime;
    private int _grabDay;
    private bool _dragging;

    public TimeGridView()
    {
        _body = new VirtualStrip(Orientation.Horizontal, scrollBars: true, NewColumn, (column, index) => BindColumn((Grid)column, index));
        _headers = new VirtualStrip(Orientation.Horizontal, scrollBars: false, () => new Border(), (header, index) => BindHeader((Border)header, index))
        {
            Height = 52,
        };

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _laneScroll.Content = _laneCanvas;
        _weekLabel.Style = Theme.Style("CaptionTextBlockStyle");
        _weekLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _gutterScroll.Content = BuildGutter();
        Add(root, _headers, 0, 1);
        Add(root, _laneScroll, 1, 1);
        Add(root, _gutterScroll, 2, 0);
        Add(root, _weekLabel, 0, 0);
        Add(root, _body, 2, 1);
        Overlay = ViewParts.Overlay(root, 3, 2);
        Content = root;

        Scroll.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0)
            {
                Relayout();
            }
            _headers.Scroll.ScrollToHorizontalOffset(Scroll.HorizontalOffset);
            _laneScroll.ScrollToHorizontalOffset(Scroll.HorizontalOffset);
            _gutterScroll.ScrollToVerticalOffset(Scroll.VerticalOffset);
            if (_columnWidth > 0 && e.HorizontalChange != 0)
            {
                _anchor = _first.AddDays((int)Math.Round(Scroll.HorizontalOffset / _columnWidth));
                _anchorToNow = false;
            }
            ShowLane();
        };

        // A click on the empty all-day lane creates an all-day event that day (R5 interim).
        _laneCanvas.MouseLeftButtonUp += (_, e) =>
        {
            if (_columnWidth > 0)
            {
                var day = _first.AddDays((int)(e.GetPosition(_laneCanvas).X / _columnWidth));
                CreateRequested?.Invoke(day.ToDateTime(TimeOnly.MinValue), true);
            }
        };
    }

    public event Action<DateOnly>? VisibleDayChanged;

    public event Action<DisplayEvent>? EventOpened;

    public event Action<DateTime, bool>? CreateRequested;

    public event Action<DisplayEvent, DragTarget>? EventDropped;

    public Canvas Overlay { get; }

    private ScrollViewer Scroll => _body.Scroll;

    public void SetData(DateOnly first, int days, IReadOnlyList<DisplayEvent> events, IReadOnlyDictionary<DateOnly, string> holidays)
    {
        (_first, _count, _holidays) = (first, days, holidays);
        // The header grows a line for holiday names only when there are any (calendar-shading interim).
        _headers.Height = holidays.Keys.Any(d => d >= first && d < first.AddDays(days)) ? 68 : 52;
        _timed = events.Where(e => !e.InAllDayLane)
            .SelectMany(e => Days(e).Select(d => (Day: d, Event: e)))
            .GroupBy(x => x.Day)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Event).ToList());
        _laneEvents = events.Where(e => e.InAllDayLane).ToList();
        _laneSpans = _laneEvents.Select(e => (
            Math.Max(0, e.FirstDay.DayNumber - first.DayNumber),
            Math.Min(days - 1, e.LastDay.DayNumber - first.DayNumber))).ToArray();
        _laneRows = EventLayout.Lanes(_laneSpans);
        Relayout(force: true);
    }

    public void ScrollTo(DateOnly day, bool toNow)
    {
        _anchor = day;
        _anchorToNow = toNow;
        ApplyAnchor();
    }

    public void Reveal(DateOnly day, DateTime? start) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        if (_columnWidth <= 0)
        {
            return;
        }
        var index = day.DayNumber - _first.DayNumber;
        var left = (int)Math.Round(Scroll.HorizontalOffset / _columnWidth);
        if (index < left)
        {
            Scroll.ScrollToHorizontalOffset(index * _columnWidth);
        }
        else if (index > left + VisibleDays - 1)
        {
            Scroll.ScrollToHorizontalOffset((index - VisibleDays + 1) * _columnWidth);
        }
        if (start is { } s)
        {
            var top = DayColumnPanel.PixelsFor(s.TimeOfDay.TotalMinutes);
            if (top < Scroll.VerticalOffset || top + 48 > Scroll.VerticalOffset + Scroll.ViewportHeight)
            {
                Scroll.ScrollToVerticalOffset(Math.Max(0, top - 48));
            }
        }
    });

    public void Page(int direction) =>
        Scroll.ScrollToHorizontalOffset(Math.Max(0, Scroll.HorizontalOffset + direction * VisibleDays * _columnWidth));

    public bool UpdateNowLine()
    {
        var now = DateTime.Now;
        if (_nowLine?.Tag is DateOnly day && day != DateOnly.FromDateTime(now))
        {
            return false;
        }
        if (_nowLine is not null)
        {
            _nowLine.Margin = new Thickness(0, DayColumnPanel.PixelsFor(now.TimeOfDay.TotalMinutes) - 1, 0, 0);
        }
        return true;
    }

    /// <summary>Recomputes the column width from the viewport; the columns are rebound with it and the data.</summary>
    private void Relayout(bool force = false)
    {
        var viewport = Scroll.ViewportWidth > 0 ? Scroll.ViewportWidth : _body.ActualWidth;
        if (viewport <= 0)
        {
            return;
        }
        var width = Math.Max(VisibleDays * 40, viewport) / VisibleDays;
        if (!force && Math.Abs(width - _columnWidth) < 0.5)
        {
            return;
        }
        _columnWidth = width;
        _laneCanvas.Width = _count * _columnWidth;
        foreach (var card in _laneCards.Values)
        {
            _laneCanvas.Children.Remove(card);
        }
        _laneCards.Clear();
        _body.SetCount(_count);
        _headers.SetCount(_count);
        ShowLane();
        ApplyAnchor();
    }

    private void ApplyAnchor()
    {
        if (_anchor is not { } day || _columnWidth <= 0)
        {
            return;
        }
        var x = (day.DayNumber - _first.DayNumber) * _columnWidth;
        double? y = _anchorToNow ? DayColumnPanel.PixelsFor(Math.Max(0, DateTime.Now.TimeOfDay.TotalMinutes - 60)) : null;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            Scroll.ScrollToHorizontalOffset(x);
            if (y is { } top)
            {
                Scroll.ScrollToVerticalOffset(top);
            }
        });
    }

    /// <summary>The all-day lane's cards for the days near the viewport; its height from the rows they use.</summary>
    private void ShowLane()
    {
        if (_columnWidth <= 0)
        {
            return;
        }
        var (a, b) = ViewPeriod.Realized(Scroll.HorizontalOffset, Scroll.ViewportWidth, _columnWidth, _count, VisibleDays);
        var rows = 0;
        for (var j = 0; j < _laneEvents.Count; j++)
        {
            var (first, last) = _laneSpans[j];
            var inRange = last >= a && first <= b;
            if (inRange && !_laneCards.ContainsKey(j))
            {
                var card = EventCard.Create(_laneEvents[j], Theme.Calendar(_laneEvents[j].Collection.Colour), LaneHeight, chip: true);
                EventDrag.Attach(card, _laneEvents[j], this);
                card.Width = (last - first + 1) * _columnWidth - 2;
                ViewParts.Place(card, first * _columnWidth, 2 + _laneRows[j] * (LaneHeight + 2));
                _laneCanvas.Children.Add(card);
                _laneCards[j] = card;
            }
            else if (!inRange && !_dragging && _laneCards.Remove(j, out var old))
            {
                _laneCanvas.Children.Remove(old);
            }
            if (inRange)
            {
                rows = Math.Max(rows, _laneRows[j] + 1);
            }
        }
        _laneCanvas.Height = rows == 0 ? 12 : rows * (LaneHeight + 2) + 2;

        var middle = _first.AddDays((int)((Scroll.HorizontalOffset + Scroll.ViewportWidth / 2) / _columnWidth));
        _weekLabel.Text = ViewParts.ShowWeekNumbers ? ViewParts.WeekLabel(middle, _first.DayOfWeek) : "";
        VisibleDayChanged?.Invoke(middle);
    }

    private void BindHeader(Border header, int index)
    {
        var date = _first.AddDays(index);
        header.Width = _columnWidth;
        header.Child = ViewParts.DayHeader(date, date == DateOnly.FromDateTime(DateTime.Now), _holidays.GetValueOrDefault(date));
    }

    /// <summary>A day column: shade, hour lines, off-hours, events side by side (R4) and the current-time line.</summary>
    private void BindColumn(Grid column, int index)
    {
        var date = _first.AddDays(index);
        var isToday = date == DateOnly.FromDateTime(DateTime.Now);
        if (_nowLine is not null && _nowLine.Parent == column)
        {
            _nowLine = null;
        }
        column.Children.Clear();
        column.Width = _columnWidth;
        column.Height = DayHeight;
        column.Background = ViewParts.DayShade(date, isToday, _holidays.ContainsKey(date)) ?? ViewParts.Clickable;
        column.Tag = date;

        // The left divider.
        column.Children.Add(new Rectangle { Width = 1, Fill = Theme.Brush("DividerBrush"), HorizontalAlignment = HorizontalAlignment.Left });

        // A working day: the time before Start and after End is shaded (calendar-shading R2), under the hour lines.
        var hours = ViewParts.Working;
        if (hours.IsWorkingDay(date) && !_holidays.ContainsKey(date))
        {
            var before = DayColumnPanel.PixelsFor(hours.Start.Hour * 60 + hours.Start.Minute);
            var after = DayColumnPanel.PixelsFor(hours.End.Hour * 60 + hours.End.Minute);
            var brush = Theme.Brush("ShadeNonWorkingBrush");
            column.Children.Add(new Rectangle { Height = before, Fill = brush, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false });
            column.Children.Add(new Rectangle
            {
                Height = Math.Max(0, DayHeight - after), Fill = brush, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
            });
        }
        var lines = new Canvas { IsHitTestVisible = false };
        for (var h = 0; h < 24; h++)
        {
            var line = new Rectangle { Height = 1, Width = _columnWidth, Fill = Theme.Brush("DividerBrush") };
            ViewParts.Place(line, 0, DayColumnPanel.PixelsFor(h * 60));
            lines.Children.Add(line);
        }
        column.Children.Add(lines);

        var panel = new DayColumnPanel { Margin = new Thickness(1, 0, 0, 0) };
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var events = _timed.GetValueOrDefault(date) ?? [];
        var intervals = events.Select(e =>
        {
            var s = Math.Max(0, (e.Start - dayStart).TotalMinutes);
            var en = Math.Min(24 * 60, (e.End - dayStart).TotalMinutes);
            return (s, Math.Max(en, s + 15)); // zero-length events still get a visible slot
        }).ToList();
        var columns = EventLayout.Columns(intervals);
        for (var i = 0; i < events.Count; i++)
        {
            var (s, en) = intervals[i];
            var height = DayColumnPanel.PixelsFor(en) - DayColumnPanel.PixelsFor(s) - 1;
            var card = EventCard.Create(events[i], Theme.Calendar(events[i].Collection.Colour), height);
            AddResizeHandles(card, events[i], dayStart, height);
            EventDrag.Attach(card, events[i], this);
            DayColumnPanel.Place(card, s, en, columns[i].Column, columns[i].Columns);
            panel.Children.Add(card);
        }
        column.Children.Add(panel);

        if (isToday)
        {
            _nowLine = new Rectangle { Height = 2, Fill = Theme.Brush("NowLineBrush"), VerticalAlignment = VerticalAlignment.Top, Tag = date, IsHitTestVisible = false };
            column.Children.Add(_nowLine);
            UpdateNowLine();
        }
    }

    /// <summary>An empty day column; a click on an empty slot creates a 1-hour event at that half hour (R5 interim).</summary>
    private Grid NewColumn()
    {
        var column = new Grid();
        column.MouseLeftButtonUp += (_, e) =>
        {
            if (column.Tag is DateOnly date)
            {
                var minutes = Math.Floor(e.GetPosition(column).Y / DayColumnPanel.HourHeight * 2) * 30;
                CreateRequested?.Invoke(date.ToDateTime(TimeOnly.MinValue).AddMinutes(Math.Clamp(minutes, 0, 23.5 * 60)), false);
            }
        };
        return column;
    }

    /// <summary>The top edge of the piece with the start and the bottom edge of the piece with the end resize it (R7).</summary>
    private static void AddResizeHandles(Border card, DisplayEvent e, DateTime dayStart, double height)
    {
        if (!e.Editable || height < 16)
        {
            return;
        }
        var content = card.Child;
        card.Child = null;
        var layers = new Grid();
        if (content is not null)
        {
            layers.Children.Add(content);
        }
        if (e.Start >= dayStart)
        {
            layers.Children.Add(new ResizeHandle(DragMode.ResizeStart) { Margin = new Thickness(0, -1, 0, 0) });
        }
        if (e.End <= dayStart.AddDays(1))
        {
            layers.Children.Add(new ResizeHandle(DragMode.ResizeEnd) { Margin = new Thickness(0, 0, 0, -1) });
        }
        card.Child = layers;
    }

    // ---- Drag (R7): positions in the whole strip of days give the day (x) and time (y); above the body is the all-day lane. ----

    private Point InDays(Point window)
    {
        var p = ViewParts.To(Scroll, window);
        return new Point(p.X + Scroll.HorizontalOffset, p.Y + Scroll.VerticalOffset);
    }

    private int DayIndexAt(Point days) => Math.Clamp((int)Math.Floor(days.X / _columnWidth), 0, _count - 1);

    private DateTime TimeAt(Point days) =>
        _first.AddDays(DayIndexAt(days)).ToDateTime(TimeOnly.MinValue)
            .AddMinutes(Math.Clamp(days.Y / DayColumnPanel.HourHeight * 60, 0, 24 * 60));

    public void BeginDrag(DisplayEvent e, DragMode mode, Point window)
    {
        _dragging = true;
        _body.KeepAll = true;
        var days = InDays(window);
        _grabTime = TimeAt(days);
        _grabDay = DayIndexAt(days);
    }

    public DragTarget? Target(DisplayEvent e, DragMode mode, Point window)
    {
        if (_columnWidth <= 0)
        {
            return null;
        }
        var days = InDays(window);
        var inLane = ViewParts.To(Scroll, window).Y < 0;
        if (e.InAllDayLane)
        {
            if (inLane)
            {
                var (s, en) = DragMath.MoveDays(e.Start, e.End, DayIndexAt(days) - _grabDay);
                return new DragTarget(s, en, e.AllDay, LaneGhost(s, en));
            }
            var (ts, te) = DragMath.ToTimed(TimeAt(days));
            return new DragTarget(ts, te, false, GridGhost(ts, te, ts));
        }
        if (inLane && mode == DragMode.Move)
        {
            var (s, en) = DragMath.ToAllDay(DateOnly.FromDateTime(TimeAt(days)));
            return new DragTarget(s, en, true, LaneGhost(s, en));
        }
        var (a, b) = mode switch
        {
            DragMode.ResizeStart => DragMath.ResizeStart(e.Start, e.End, TimeAt(days)),
            DragMode.ResizeEnd => DragMath.ResizeEnd(e.Start, e.End, TimeAt(days)),
            _ => DragMath.Move(e.Start, e.End, _grabTime, TimeAt(days)),
        };
        return new DragTarget(a, b, false, GridGhost(a, b, mode == DragMode.ResizeEnd ? b.AddTicks(-1) : a));
    }

    /// <summary>The part of [start, end) on the anchor's day, in the overlay.</summary>
    private Rect GridGhost(DateTime start, DateTime end, DateTime anchor)
    {
        var dayStart = anchor.Date;
        var top = start > dayStart ? start : dayStart;
        var bottom = end < dayStart.AddDays(1) ? end : dayStart.AddDays(1);
        var index = DateOnly.FromDateTime(dayStart).DayNumber - _first.DayNumber;
        var y = DayColumnPanel.PixelsFor((top - dayStart).TotalMinutes);
        var rect = new Rect(index * _columnWidth + 2 - Scroll.HorizontalOffset, y - Scroll.VerticalOffset, _columnWidth - 4,
            Math.Max(12, DayColumnPanel.PixelsFor((bottom - top).TotalMinutes)));
        return ViewParts.InOverlay(Scroll, Overlay, rect);
    }

    private Rect LaneGhost(DateTime start, DateTime end)
    {
        var first = DateOnly.FromDateTime(start).DayNumber - _first.DayNumber;
        var last = DateOnly.FromDateTime(end > start ? end.AddTicks(-1) : start).DayNumber - _first.DayNumber;
        var rect = new Rect(first * _columnWidth, 2, (last - first + 1) * _columnWidth - 2, LaneHeight);
        return ViewParts.InOverlay(_laneCanvas, Overlay, rect);
    }

    public void AutoScroll(Point window)
    {
        var v = ViewParts.To(Scroll, window);
        var dx = ViewParts.EdgeStep(v.X, Scroll.ViewportWidth);
        var dy = v.Y < 0 ? 0 : ViewParts.EdgeStep(v.Y, Scroll.ViewportHeight);
        if (dx != 0)
        {
            Scroll.ScrollToHorizontalOffset(Scroll.HorizontalOffset + dx);
        }
        if (dy != 0)
        {
            Scroll.ScrollToVerticalOffset(Scroll.VerticalOffset + dy);
        }
    }

    public void EndDrag()
    {
        _dragging = false;
        _body.KeepAll = false;
        ShowLane();
    }

    public void CompleteDrag(DisplayEvent e, DragTarget target) => EventDropped?.Invoke(e, target);

    public void OpenEvent(DisplayEvent e) => EventOpened?.Invoke(e);

    private Grid BuildGutter()
    {
        var gutter = new Grid { Height = DayHeight };
        for (var h = 1; h < 24; h++)
        {
            var label = new TextBlock
            {
                Text = DateTime.Today.AddHours(h).ToString("t"),
                Style = Theme.Style("CaptionTextBlockStyle"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, DayColumnPanel.PixelsFor(h * 60) - 8, 8, 0),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            gutter.Children.Add(label);
        }
        return gutter;
    }

    /// <summary>The days a timed event touches (it is clipped to each).</summary>
    private static IEnumerable<DateOnly> Days(DisplayEvent e)
    {
        for (var d = e.FirstDay; d <= e.LastDay; d = d.AddDays(1))
        {
            yield return d;
        }
    }

    private static void Add(Grid grid, FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
