using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Views;

/// <summary>
/// Agenda view (agenda-view R2-R5; cards fit their content, R4a): one column per day, each a plain list (all-day items first, then by start
/// time), seven columns filling the width. Scrolls horizontally through days and vertically through the lists;
/// day headers stay at the top. Only days near the viewport are built.
/// </summary>
internal sealed partial class AgendaView : UserControl, IScrollingView, IDragHost
{
    private const int VisibleDays = 7;
    private const int Buffer = 7;

    private readonly ScrollViewer _headerScroll = ViewParts.FollowerStrip(horizontal: true);
    private readonly ScrollViewer _bodyScroll = new()
    {
        HorizontalScrollMode = ScrollMode.Enabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        ZoomMode = ZoomMode.Disabled,
    };
    private readonly Canvas _headerCanvas = new() { Height = 68 };
    private IReadOnlyDictionary<DateOnly, string> _holidays = new Dictionary<DateOnly, string>();
    private readonly Canvas _bodyCanvas = new();
    private readonly Dictionary<int, (UIElement Header, FrameworkElement Column)> _days = [];

    private DateOnly _first;
    private int _count;
    private Dictionary<DateOnly, List<DisplayEvent>> _byDay = [];
    private double _columnWidth;
    private DateOnly? _anchor;
    private int _grabIndex;
    private bool _dragging;

    public AgendaView()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _headerScroll.Content = _headerCanvas;
        _bodyScroll.Content = _bodyCanvas;
        Grid.SetRow(_bodyScroll, 1);
        root.Children.Add(_headerScroll);
        root.Children.Add(_bodyScroll);
        Overlay = ViewParts.Overlay(root, 2, 1);
        Content = root;

        _bodyScroll.ViewChanging += (_, e) => _headerScroll.ChangeView(e.NextView.HorizontalOffset, null, null, true);
        _bodyScroll.ViewChanged += (_, _) =>
        {
            if (_columnWidth > 0)
            {
                _anchor = _first.AddDays((int)Math.Round(_bodyScroll.HorizontalOffset / _columnWidth));
            }
            Realize();
        };
        _bodyScroll.SizeChanged += (_, _) => Relayout();
    }

    public event Action<DateOnly>? VisibleDayChanged;

    public event Action<DisplayEvent>? EventOpened;

    // Agenda has no empty slots to click.
    public event Action<DateTime, bool>? CreateRequested { add { } remove { } }

    public event Action<DisplayEvent, DragTarget>? EventDropped;

    public Canvas Overlay { get; }

    public void SetData(DateOnly first, int days, IReadOnlyList<DisplayEvent> events, IReadOnlyDictionary<DateOnly, string> holidays)
    {
        (_first, _count, _holidays) = (first, days, holidays);
        // Multi-day events appear on each day they span (R5).
        _byDay = events
            .SelectMany(e => Enumerable.Range(0, e.LastDay.DayNumber - e.FirstDay.DayNumber + 1)
                .Select(n => (Day: e.FirstDay.AddDays(n), Event: e)))
            .GroupBy(x => x.Day)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Event)
                .OrderBy(e => e.InAllDayLane ? 0 : 1).ThenBy(e => e.Start).ToList());
        Relayout(force: true);
    }

    public void ScrollTo(DateOnly day, bool toNow)
    {
        _anchor = day;
        ApplyAnchor(toTop: true);
    }

    public bool UpdateNowLine() => true;

    private void Relayout(bool force = false)
    {
        var width = Math.Max(VisibleDays * 40, _bodyScroll.ActualWidth) / VisibleDays;
        if (_bodyScroll.ActualWidth <= 0 || !force && Math.Abs(width - _columnWidth) < 0.5)
        {
            return;
        }
        _columnWidth = width;
        foreach (var (header, column) in _days.Values)
        {
            _headerCanvas.Children.Remove(header);
            _bodyCanvas.Children.Remove(column);
        }
        _days.Clear();
        _headerCanvas.Width = _bodyCanvas.Width = _count * _columnWidth;
        Realize();
        ApplyAnchor(toTop: false);
    }

    private void ApplyAnchor(bool toTop)
    {
        if (_anchor is not { } day || _columnWidth <= 0)
        {
            return;
        }
        var x = (day.DayNumber - _first.DayNumber) * _columnWidth;
        DispatcherQueue.TryEnqueue(() => _bodyScroll.ChangeView(x, toTop ? 0 : null, null, true));
    }

    private void Realize()
    {
        if (_columnWidth <= 0)
        {
            return;
        }
        var (a, b) = ViewPeriod.Realized(_bodyScroll.HorizontalOffset, _bodyScroll.ViewportWidth, _columnWidth, _count, Buffer);
        foreach (var index in _days.Keys.Where(i => !_dragging && (i < a || i > b)).ToList())
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
            var label = date == today ? "Today" : date == today.AddDays(1) ? "Tomorrow" : null;
            if (_holidays.GetValueOrDefault(date) is { } holiday)
            {
                label = label is null ? holiday : $"{label} · {holiday}";
            }
            var header = ViewParts.DayHeader(date, date == today, label, "d MMM");
            header.Width = _columnWidth;
            ViewParts.Place(header, i * _columnWidth, 0);
            _headerCanvas.Children.Add(header);

            var column = BuildList(date, date == today);
            ViewParts.Place(column, i * _columnWidth, 0);
            _bodyCanvas.Children.Add(column);
            _days[i] = (header, column);
        }

        // The lists scroll together: the canvas is as tall as the longest built list, and at least the viewport.
        var tallest = _days.Values.Select(d => d.Column.DesiredSize.Height).DefaultIfEmpty(0).Max();
        var height = Math.Max(_bodyScroll.ViewportHeight, tallest);
        _bodyCanvas.Height = height;
        foreach (var (_, column) in _days.Values)
        {
            column.MinHeight = height;
        }

        var middle = _first.AddDays((int)((_bodyScroll.HorizontalOffset + _bodyScroll.ViewportWidth / 2) / _columnWidth));
        VisibleDayChanged?.Invoke(middle);
    }

    // ---- Drag (R8, calendar-views R7): to another day column, keeping the time and length. ----

    private int DayIndexAt(Windows.Foundation.Point canvas) => Math.Clamp((int)Math.Floor(canvas.X / _columnWidth), 0, _count - 1);

    public void BeginDrag(DisplayEvent e, DragMode mode, Windows.Foundation.Point window)
    {
        _dragging = true;
        _grabIndex = DayIndexAt(ViewParts.To(_bodyCanvas, window));
    }

    public DragTarget? Target(DisplayEvent e, DragMode mode, Windows.Foundation.Point window)
    {
        if (_columnWidth <= 0)
        {
            return null;
        }
        var canvas = ViewParts.To(_bodyCanvas, window);
        var index = DayIndexAt(canvas);
        var (s, en) = DragMath.MoveDays(e.Start, e.End, index - _grabIndex);
        var rect = new Windows.Foundation.Rect(index * _columnWidth + 4, canvas.Y - 16, _columnWidth - 8, 32);
        return new DragTarget(s, en, e.AllDay, ViewParts.InOverlay(_bodyCanvas, Overlay, rect));
    }

    public void AutoScroll(Windows.Foundation.Point window)
    {
        var v = ViewParts.To(_bodyScroll, window);
        var dx = ViewParts.EdgeStep(v.X, _bodyScroll.ActualWidth);
        var dy = ViewParts.EdgeStep(v.Y, _bodyScroll.ActualHeight);
        if (dx != 0 || dy != 0)
        {
            _bodyScroll.ChangeView(_bodyScroll.HorizontalOffset + dx, _bodyScroll.VerticalOffset + dy, null, true);
        }
    }

    public void EndDrag()
    {
        _dragging = false;
        Realize();
    }

    public void CompleteDrag(DisplayEvent e, DragTarget target) => EventDropped?.Invoke(e, target);

    public void OpenEvent(DisplayEvent e) => EventOpened?.Invoke(e);

    private StackPanel BuildList(DateOnly date, bool isToday)
    {
        var list = new StackPanel
        {
            Width = _columnWidth,
            Spacing = 4,
            Padding = new Thickness(4, 4, 4, 8),
            BorderBrush = Theme.Brush("DividerBrush"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Background = ViewParts.DayShade(date, isToday, _holidays.ContainsKey(date)),
        };
        var events = _byDay.GetValueOrDefault(date) ?? [];
        foreach (var e in events)
        {
            var total = e.LastDay.DayNumber - e.FirstDay.DayNumber + 1;
            var details = total > 1 ? $"Day {date.DayNumber - e.FirstDay.DayNumber + 1} of {total}" : null;
            var card = EventCard.Create(e, Theme.Calendar(e.Collection.Colour), double.NaN, details: details);
            EventDrag.Attach(card, e, this);
            list.Children.Add(card);
        }
        if (events.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "Nothing scheduled",
                Style = Theme.Style("CaptionTextBlockStyle"),
                Foreground = Theme.Brush("TextSecondaryBrush"),
                Margin = new Thickness(4, 4, 0, 0),
            });
        }
        list.Measure(new Windows.Foundation.Size(_columnWidth, double.PositiveInfinity));
        return list;
    }
}
