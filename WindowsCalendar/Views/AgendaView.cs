using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Views;

/// <summary>
/// Agenda view (agenda-view R2-R5; cards fit their content, R4a): one column per day, each a plain list (all-day items first, then by start
/// time), seven columns filling the width. Scrolls horizontally through days and vertically through the lists;
/// day headers stay at the top and follow the horizontal scroll. The days are fixed-width items in <see cref="VirtualStrip"/>s.
/// </summary>
internal sealed class AgendaView : UserControl, IScrollingView, IDragHost
{
    private const int VisibleDays = 7;

    private readonly VirtualStrip _body;
    private readonly VirtualStrip _headers;
    private IReadOnlyDictionary<DateOnly, string> _holidays = new Dictionary<DateOnly, string>();

    private DateOnly _first;
    private int _count;
    private Dictionary<DateOnly, List<DisplayEvent>> _byDay = [];
    private double _columnWidth;
    private DateOnly? _anchor;
    private int _grabIndex;

    public AgendaView()
    {
        _body = new VirtualStrip(Orientation.Horizontal, scrollBars: true, NewList, (list, index) => BindList((StackPanel)list, index));
        _headers = new VirtualStrip(Orientation.Horizontal, scrollBars: false, () => new Border(), (header, index) => BindHeader((Border)header, index));
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_body, 1);
        root.Children.Add(_headers);
        root.Children.Add(_body);
        Overlay = ViewParts.Overlay(root, 2, 1);
        Content = root;

        Scroll.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
            {
                Relayout();
            }
            _headers.Scroll.ScrollToHorizontalOffset(Scroll.HorizontalOffset);
            if (_columnWidth > 0)
            {
                if (e.HorizontalChange != 0)
                {
                    _anchor = _first.AddDays((int)Math.Round(Scroll.HorizontalOffset / _columnWidth));
                }
                var middle = _first.AddDays((int)((Scroll.HorizontalOffset + Scroll.ViewportWidth / 2) / _columnWidth));
                VisibleDayChanged?.Invoke(middle);
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
        _headers.Height = TextScale.Of(68);
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
    });

    public void Page(int direction) =>
        Scroll.ScrollToHorizontalOffset(Math.Max(0, Scroll.HorizontalOffset + direction * VisibleDays * _columnWidth));

    private void Relayout(bool force = false)
    {
        var viewport = ViewParts.ItemsWidth(Scroll);
        if (viewport <= 0)
        {
            return;
        }
        var width = ViewParts.WholePixels(Math.Max(VisibleDays * 40, viewport) / VisibleDays, this);
        var resized = Math.Abs(width - _columnWidth) >= 0.5;
        if (!force && !resized)
        {
            return;
        }
        _columnWidth = width;
        if (resized)
        {
            // New item size: both strips start over, so the headers stay over their days.
            Serilog.Log.Information("Agenda view column width {Width:F1}", width);
            _body.Reset(_count);
            _headers.Reset(_count);
        }
        else
        {
            _body.SetCount(_count);
            _headers.SetCount(_count);
        }
        ApplyAnchor(toTop: false);
    }

    private void ApplyAnchor(bool toTop)
    {
        if (_anchor is not { } day || _columnWidth <= 0)
        {
            return;
        }
        var x = (day.DayNumber - _first.DayNumber) * _columnWidth;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            Scroll.ScrollToHorizontalOffset(x);
            if (toTop)
            {
                Scroll.ScrollToVerticalOffset(0);
            }
        });
    }

    private void BindHeader(Border header, int index)
    {
        var date = _first.AddDays(index);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var label = date == today ? "Today" : date == today.AddDays(1) ? "Tomorrow" : null;
        if (_holidays.GetValueOrDefault(date) is { } holiday)
        {
            label = label is null ? holiday : $"{label} · {holiday}";
        }
        // The first day of each week carries its number (calendar-shading R7 interim).
        if (ViewParts.ShowWeekNumbers && date.DayOfWeek == _first.DayOfWeek)
        {
            var week = ViewParts.WeekLabel(date, _first.DayOfWeek);
            label = label is null ? week : $"{week} · {label}";
        }
        header.Width = _columnWidth;
        header.Child = ViewParts.DayHeader(date, date == today, label, "d MMM", ProblemMarks.DayIcon(date));
    }

    // ---- Drag (R8, calendar-views R7): to another day column, keeping the time and length. ----

    private Point InDays(Point window)
    {
        var p = ViewParts.To(Scroll, window);
        return new Point(p.X + Scroll.HorizontalOffset, p.Y + Scroll.VerticalOffset);
    }

    private int DayIndexAt(Point days) => Math.Clamp((int)Math.Floor(days.X / _columnWidth), 0, _count - 1);

    public void BeginDrag(DisplayEvent e, DragMode mode, Point window)
    {
        _body.KeepAll = true;
        _grabIndex = DayIndexAt(InDays(window));
    }

    public DragTarget? Target(DisplayEvent e, DragMode mode, Point window)
    {
        if (_columnWidth <= 0)
        {
            return null;
        }
        var days = InDays(window);
        var index = DayIndexAt(days);
        var (s, en) = DragMath.MoveDays(e.Start, e.End, index - _grabIndex);
        var rect = new Rect(index * _columnWidth + 4 - Scroll.HorizontalOffset, days.Y - 16 - Scroll.VerticalOffset, _columnWidth - 8, 32);
        return new DragTarget(s, en, e.AllDay, ViewParts.InOverlay(Scroll, Overlay, rect));
    }

    public void AutoScroll(Point window)
    {
        var v = ViewParts.To(Scroll, window);
        var dx = ViewParts.EdgeStep(v.X, Scroll.ViewportWidth);
        var dy = ViewParts.EdgeStep(v.Y, Scroll.ViewportHeight);
        if (dx != 0)
        {
            Scroll.ScrollToHorizontalOffset(Scroll.HorizontalOffset + dx);
        }
        if (dy != 0)
        {
            Scroll.ScrollToVerticalOffset(Scroll.VerticalOffset + dy);
        }
    }

    public void EndDrag() => _body.KeepAll = false;

    public void CompleteDrag(DisplayEvent e, DragTarget target) => EventDropped?.Invoke(e, target);

    public void OpenEvent(DisplayEvent e) => EventOpened?.Invoke(e);

    /// <summary>An empty day list; a click on its empty part creates an all-day event (calendar-views R5 interim).</summary>
    private StackPanel NewList()
    {
        var list = new StackPanel();
        list.MouseLeftButtonUp += (_, _) =>
        {
            if (list.Tag is DateOnly date)
            {
                CreateRequested?.Invoke(date.ToDateTime(TimeOnly.MinValue), true);
            }
        };
        return list;
    }

    /// <summary>Shown on empty days in place of "Nothing scheduled", e.g. during an account's first sync (ui-states R1a).</summary>
    public string? EmptyText { get; set; }

    /// <summary>One day: its events as cards that fit their content, or "Nothing scheduled". At least as tall as the viewport.</summary>
    private void BindList(StackPanel list, int index)
    {
        var date = _first.AddDays(index);
        var isToday = date == DateOnly.FromDateTime(DateTime.Now);
        list.Tag = date;
        list.Children.Clear();
        list.Width = _columnWidth;
        list.MinHeight = Scroll.ViewportHeight;
        // The divider is drawn by the first child's border, so the list stays a plain StackPanel.
        list.Background = ViewParts.DayShade(date, isToday, _holidays.ContainsKey(date)) ?? ViewParts.Clickable;
        var events = _byDay.GetValueOrDefault(date) ?? [];
        var body = new StackPanel { Margin = new Thickness(4, 4, 4, 8) };
        foreach (var e in events)
        {
            var total = e.LastDay.DayNumber - e.FirstDay.DayNumber + 1;
            var details = total > 1 ? $"Day {date.DayNumber - e.FirstDay.DayNumber + 1} of {total}" : null;
            var card = EventCard.Create(e, Theme.Calendar(e.Collection.Colour), double.NaN, details: details);
            card.Margin = new Thickness(1, 0, 1, 4);
            ProblemMarks.Decorate(card, e.Key);
            EventDrag.Attach(card, e, this);
            body.Children.Add(card);
        }
        if (events.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = EmptyText ?? "Nothing scheduled",
                Style = Theme.Style("CaptionTextBlockStyle"),
                Foreground = Theme.Brush("TextSecondaryBrush"),
                Margin = new Thickness(4, 4, 0, 0),
            });
        }
        list.Children.Add(new Border
        {
            BorderBrush = Theme.Brush("DividerBrush"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            MinHeight = Scroll.ViewportHeight,
            Child = body,
        });
    }
}
