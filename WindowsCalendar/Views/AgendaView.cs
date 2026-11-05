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
internal sealed partial class AgendaView : UserControl, IScrollingView
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
    private readonly Canvas _bodyCanvas = new();
    private readonly Dictionary<int, (UIElement Header, FrameworkElement Column)> _days = [];

    private DateOnly _first;
    private int _count;
    private Dictionary<DateOnly, List<DisplayEvent>> _byDay = [];
    private double _columnWidth;
    private DateOnly? _anchor;

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

    public event Action<string>? EventOpened;

    // Agenda has no empty slots to click.
    public event Action<DateTime, bool>? CreateRequested { add { } remove { } }

    public void SetData(DateOnly first, int days, IReadOnlyList<DisplayEvent> events)
    {
        (_first, _count) = (first, days);
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
            var label = date == today ? "Today" : date == today.AddDays(1) ? "Tomorrow" : null;
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

    private StackPanel BuildList(DateOnly date, bool isToday)
    {
        var list = new StackPanel
        {
            Width = _columnWidth,
            Spacing = 4,
            Padding = new Thickness(4, 4, 4, 8),
            BorderBrush = Theme.Brush("DividerBrush"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Background = ViewParts.DayShade(date, isToday),
        };
        var events = _byDay.GetValueOrDefault(date) ?? [];
        foreach (var e in events)
        {
            var total = e.LastDay.DayNumber - e.FirstDay.DayNumber + 1;
            var details = total > 1 ? $"Day {date.DayNumber - e.FirstDay.DayNumber + 1} of {total}" : null;
            var card = EventCard.Create(e, Theme.Calendar(e.Collection.Colour), double.NaN, details: details);
            ViewParts.OnOpen(card, e, () => EventOpened);
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
