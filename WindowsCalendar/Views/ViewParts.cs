using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WindowsCalendar.Views;

/// <summary>
/// A view that scrolls naturally over the occurrence window (calendar-views R1a). Only items near the viewport
/// are built.
/// </summary>
internal interface IScrollingView
{
    /// <summary>Replaces the data, keeping the scroll position on the same date.</summary>
    void SetData(DateOnly first, int days, System.Collections.Generic.IReadOnlyList<DisplayEvent> events);

    /// <summary>Scrolls so <paramref name="day"/> is at the start; in Week, <paramref name="toNow"/> also scrolls to the current time.</summary>
    void ScrollTo(DateOnly day, bool toNow);

    /// <summary>Moves the current-time line; false when the day has changed and the data needs refreshing.</summary>
    bool UpdateNowLine();

    /// <summary>Raised with the day in the middle of the viewport, for the period title.</summary>
    event Action<DateOnly>? VisibleDayChanged;

    /// <summary>Raised with the event (and its occurrence) when an event card is clicked (calendar-views R6b).</summary>
    event Action<DisplayEvent>? EventOpened;

    /// <summary>Raised when an empty slot is clicked: its start, and whether it is all-day (R5 interim).</summary>
    event Action<DateTime, bool>? CreateRequested;

    /// <summary>Raised when a dragged event is dropped somewhere new (R7).</summary>
    event Action<DisplayEvent, DragTarget>? EventDropped;
}

/// <summary>Building blocks shared by the views.</summary>
internal static class ViewParts
{
    /// <summary>A background that receives clicks without drawing anything.</summary>
    public static readonly Brush Clickable = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    /// <summary>A window point in an element's coordinates (scrolling included).</summary>
    public static Windows.Foundation.Point To(UIElement element, Windows.Foundation.Point window) =>
        element.TransformToVisual(null).Inverse.TransformPoint(window);

    /// <summary>A rectangle in <paramref name="from"/>'s coordinates, in the overlay's.</summary>
    public static Windows.Foundation.Rect InOverlay(UIElement from, Canvas overlay, Windows.Foundation.Rect r) =>
        from.TransformToVisual(overlay).TransformBounds(r);

    /// <summary>Auto-scroll step for a drag: towards an edge when the pointer is within 32 epx of it.</summary>
    public static double EdgeStep(double position, double length) =>
        position < 32 ? -24 : position > length - 32 ? 24 : 0;

    /// <summary>A canvas over a whole view for drag previews; it never takes clicks.</summary>
    public static Canvas Overlay(Grid root, int rows, int columns)
    {
        var overlay = new Canvas { IsHitTestVisible = false };
        Grid.SetRowSpan(overlay, rows);
        Grid.SetColumnSpan(overlay, columns);
        root.Children.Add(overlay);
        return overlay;
    }

    /// <summary>
    /// A strip that follows another ScrollViewer (day headers, all-day lane, hour labels): no scroll bars, no user
    /// scrolling of its own, moved with ChangeView.
    /// </summary>
    public static ScrollViewer FollowerStrip(bool horizontal) => new()
    {
        HorizontalScrollMode = ScrollMode.Disabled,
        VerticalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden,
        ZoomMode = ZoomMode.Disabled,
    };

    /// <summary>
    /// A day header: weekday, then the date (in a chip for today), and an optional label such as "Tomorrow".
    /// </summary>
    public static StackPanel DayHeader(DateOnly date, bool isToday, string? label = null, string dateFormat = "%d")
    {
        var number = new TextBlock
        {
            Text = date.ToString(dateFormat),
            Style = Theme.Style("BodyStrongTextBlockStyle"),
            Foreground = isToday ? Theme.Brush("TodayChipTextBrush") : Theme.Brush("TextPrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var stack = new StackPanel { Padding = new Thickness(0, 4, 0, 4), Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = date.ToString("ddd"),
            Style = Theme.Style("CaptionTextBlockStyle"),
            Foreground = Theme.Brush("TextSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(isToday
            ? new Border
            {
                Background = Theme.Brush("TodayChipBrush"),
                CornerRadius = new CornerRadius(12),
                MinWidth = 28,
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = number,
            }
            : number);
        if (label is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = label,
                Style = Theme.Style("CaptionTextBlockStyle"),
                Foreground = Theme.Brush("TextSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        AutomationProperties.SetName(stack, label is null ? date.ToString("D") : $"{label}, {date:D}");
        return stack;
    }

    public static void Place(UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
    }

    /// <summary>
    /// Day background: today a faint blue; Saturday and Sunday the non-working shade (calendar-shading interim,
    /// until working days are configurable).
    /// </summary>
    public static Brush? DayShade(DateOnly date, bool isToday) =>
        isToday ? Theme.Brush("ShadeTodayBrush")
        : date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? Theme.Brush("ShadeNonWorkingBrush")
        : null;
}
