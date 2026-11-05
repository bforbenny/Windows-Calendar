using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Views;

/// <summary>
/// Week view (calendar-views R1a, R3, R4, R4a): a continuous strip of day columns, seven filling the width, that
/// scrolls horizontally through days and vertically through hours. Day headers and the all-day lane follow the
/// horizontal scroll; hour labels follow the vertical scroll. Only days near the viewport are built.
/// </summary>
internal sealed partial class TimeGridView : UserControl, IScrollingView
{
    private const double GutterWidth = 56;
    private const double LaneHeight = 22;
    private const int VisibleDays = 7;
    private const int Buffer = 7;

    private readonly ScrollViewer _headerScroll = ViewParts.FollowerStrip(horizontal: true);
    private readonly ScrollViewer _laneScroll = ViewParts.FollowerStrip(horizontal: true);
    private readonly ScrollViewer _gutterScroll = ViewParts.FollowerStrip(horizontal: false);
    private readonly ScrollViewer _bodyScroll = new()
    {
        HorizontalScrollMode = ScrollMode.Enabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        ZoomMode = ZoomMode.Disabled,
    };
    private readonly Canvas _headerCanvas = new() { Height = 52 };
    private readonly Canvas _laneCanvas = new();
    private readonly Canvas _bodyCanvas = new() { Height = DayColumnPanel.PixelsFor(24 * 60) };
    private readonly Canvas _hourLines = new() { Height = DayColumnPanel.PixelsFor(24 * 60) };

    private readonly Dictionary<int, (UIElement Header, UIElement Column)> _days = [];
    private readonly Dictionary<int, UIElement> _laneCards = [];

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
    private DateOnly _nowDay;

    public TimeGridView()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _headerScroll.Content = _headerCanvas;
        _laneScroll.Content = _laneCanvas;
        _gutterScroll.Content = BuildGutter();
        _bodyCanvas.Children.Add(_hourLines);
        _bodyScroll.Content = _bodyCanvas;
        Add(root, _headerScroll, 0, 1);
        Add(root, _laneScroll, 1, 1);
        Add(root, _gutterScroll, 2, 0);
        Add(root, _bodyScroll, 2, 1);
        Content = root;

        _bodyScroll.ViewChanging += (_, e) =>
        {
            _headerScroll.ChangeView(e.NextView.HorizontalOffset, null, null, true);
            _laneScroll.ChangeView(e.NextView.HorizontalOffset, null, null, true);
            _gutterScroll.ChangeView(null, e.NextView.VerticalOffset, null, true);
        };
        _bodyScroll.ViewChanged += (_, _) =>
        {
            if (_columnWidth > 0)
            {
                _anchor = _first.AddDays((int)Math.Round(_bodyScroll.HorizontalOffset / _columnWidth));
                _anchorToNow = false;
            }
            Realize();
        };
        _bodyScroll.SizeChanged += (_, _) => Relayout();
    }

    public event Action<DateOnly>? VisibleDayChanged;

    public void SetData(DateOnly first, int days, IReadOnlyList<DisplayEvent> events)
    {
        (_first, _count) = (first, days);
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

    public bool UpdateNowLine()
    {
        var now = DateTime.Now;
        if (_nowLine is not null && DateOnly.FromDateTime(now) != _nowDay)
        {
            return false;
        }
        if (_nowLine is not null)
        {
            _nowLine.Margin = new Thickness(0, DayColumnPanel.PixelsFor(now.TimeOfDay.TotalMinutes) - 1, 0, 0);
        }
        return true;
    }

    /// <summary>Recomputes the column width from the viewport and rebuilds what is shown.</summary>
    private void Relayout(bool force = false)
    {
        var width = Math.Max(VisibleDays * 40, _bodyScroll.ActualWidth) / VisibleDays;
        if (_bodyScroll.ActualWidth <= 0 || !force && Math.Abs(width - _columnWidth) < 0.5)
        {
            return;
        }
        _columnWidth = width;
        Clear();
        _headerCanvas.Width = _laneCanvas.Width = _bodyCanvas.Width = _count * _columnWidth;
        _hourLines.Children.Clear();
        for (var h = 0; h < 24; h++)
        {
            var line = new Rectangle { Height = 1, Width = _bodyCanvas.Width, Fill = Theme.Brush("DividerBrush") };
            ViewParts.Place(line, 0, DayColumnPanel.PixelsFor(h * 60));
            _hourLines.Children.Add(line);
        }
        Realize();
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
        DispatcherQueue.TryEnqueue(() => _bodyScroll.ChangeView(x, y, null, true));
    }

    private void Clear()
    {
        foreach (var (header, column) in _days.Values)
        {
            _headerCanvas.Children.Remove(header);
            _bodyCanvas.Children.Remove(column);
        }
        _days.Clear();
        foreach (var card in _laneCards.Values)
        {
            _laneCanvas.Children.Remove(card);
        }
        _laneCards.Clear();
        _nowLine = null;
    }

    /// <summary>Builds the days near the viewport and removes the rest.</summary>
    private void Realize()
    {
        if (_columnWidth <= 0)
        {
            return;
        }
        var (a, b) = ViewPeriod.Realized(_bodyScroll.HorizontalOffset, _bodyScroll.ViewportWidth, _columnWidth, _count, Buffer);

        foreach (var index in _days.Keys.Where(i => i < a || i > b).ToList())
        {
            _headerCanvas.Children.Remove(_days[index].Header);
            _bodyCanvas.Children.Remove(_days[index].Column);
            _days.Remove(index);
        }
        var today = DateOnly.FromDateTime(DateTime.Now);
        for (var i = a; i <= b; i++)
        {
            if (_days.ContainsKey(i))
            {
                continue;
            }
            var date = _first.AddDays(i);
            var header = ViewParts.DayHeader(date, date == today);
            header.Width = _columnWidth;
            ViewParts.Place(header, i * _columnWidth, 0);
            _headerCanvas.Children.Add(header);
            var column = BuildColumn(date, date == today);
            ViewParts.Place(column, i * _columnWidth, 0);
            _bodyCanvas.Children.Add(column);
            _days[i] = (header, column);
        }

        // All-day lane: cards overlapping the built days; height from the rows they use.
        var rows = 0;
        for (var j = 0; j < _laneEvents.Count; j++)
        {
            var (first, last) = _laneSpans[j];
            var inRange = last >= a && first <= b;
            if (inRange && !_laneCards.ContainsKey(j))
            {
                var card = EventCard.Create(_laneEvents[j], Theme.Calendar(_laneEvents[j].Collection.Colour), LaneHeight, chip: true);
                card.Width = (last - first + 1) * _columnWidth - 2;
                ViewParts.Place(card, first * _columnWidth, 2 + _laneRows[j] * (LaneHeight + 2));
                _laneCanvas.Children.Add(card);
                _laneCards[j] = card;
            }
            else if (!inRange && _laneCards.Remove(j, out var old))
            {
                _laneCanvas.Children.Remove(old);
            }
            if (inRange)
            {
                rows = Math.Max(rows, _laneRows[j] + 1);
            }
        }
        _laneCanvas.Height = rows == 0 ? 0 : rows * (LaneHeight + 2) + 2;

        var middle = _first.AddDays((int)((_bodyScroll.HorizontalOffset + _bodyScroll.ViewportWidth / 2) / _columnWidth));
        VisibleDayChanged?.Invoke(middle);
    }

    private Grid BuildColumn(DateOnly date, bool isToday)
    {
        var column = new Grid
        {
            Width = _columnWidth,
            Height = _bodyCanvas.Height,
            BorderBrush = Theme.Brush("DividerBrush"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Background = ViewParts.DayShade(date, isToday),
        };

        var panel = new DayColumnPanel();
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
            DayColumnPanel.Place(card, s, en, columns[i].Column, columns[i].Columns);
            panel.Children.Add(card);
        }
        column.Children.Add(panel);

        if (isToday)
        {
            _nowLine = new Rectangle { Height = 2, Fill = Theme.Brush("NowLineBrush"), VerticalAlignment = VerticalAlignment.Top };
            _nowDay = date;
            column.Children.Add(_nowLine);
            UpdateNowLine();
        }
        return column;
    }

    private static Grid BuildGutter()
    {
        var gutter = new Grid { Height = DayColumnPanel.PixelsFor(24 * 60) };
        for (var h = 1; h < 24; h++)
        {
            gutter.Children.Add(new TextBlock
            {
                Text = DateTime.Today.AddHours(h).ToString("t"),
                Style = Theme.Style("CaptionTextBlockStyle"),
                Foreground = Theme.Brush("TextSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, DayColumnPanel.PixelsFor(h * 60) - 8, 8, 0),
            });
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
