using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsCalendar.Views;

/// <summary>
/// A view that scrolls naturally over the occurrence window (calendar-views R1a). Only items near the viewport
/// are built.
/// </summary>
internal interface IScrollingView
{
    /// <summary>Replaces the data, keeping the scroll position on the same date. Holidays are names by day (calendar-shading R4).</summary>
    void SetData(DateOnly first, int days, System.Collections.Generic.IReadOnlyList<DisplayEvent> events,
        System.Collections.Generic.IReadOnlyDictionary<DateOnly, string> holidays);

    /// <summary>Scrolls so <paramref name="day"/> is at the start; in Week, <paramref name="toNow"/> also scrolls to the current time.</summary>
    void ScrollTo(DateOnly day, bool toNow);

    /// <summary>Moves the current-time line; false when the day has changed and the data needs refreshing.</summary>
    bool UpdateNowLine();

    /// <summary>
    /// Scrolls just enough to show <paramref name="day"/> (and, in Week, the time <paramref name="start"/>), for the
    /// keyboard selection (keyboard-navigation interim). Runs after any pending anchor scroll.
    /// </summary>
    void Reveal(DateOnly day, DateTime? start);

    /// <summary>Scrolls one screen back (-1) or forward (+1) (keyboard-navigation R2, PgUp / PgDn).</summary>
    void Page(int direction);

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
    /// <summary>Working days and hours (calendar-shading R1), set from the store before each render.</summary>
    public static WindowsCalendar.Core.Store.WorkingHours Working { get; set; } = WindowsCalendar.Core.Store.WorkingHours.Default;

    /// <summary>"Show week numbers" (calendar-shading R7), set from the store before each render.</summary>
    public static bool ShowWeekNumbers { get; set; }

    /// <summary>The secondary timezone ruler's zone, null when off (timezone-handling R8), set from the store before each render.</summary>
    public static TimeZoneInfo? SecondaryZone { get; set; }

    /// <summary>The window's content; drag positions are in its coordinates ("window" points).</summary>
    public static UIElement? Root { get; set; }

    /// <summary>"Wk 45" for the week holding <paramref name="date"/>.</summary>
    public static string WeekLabel(DateOnly date, DayOfWeek weekStart) =>
        $"Wk {WindowsCalendar.Core.Views.ViewPeriod.WeekNumber(date, weekStart)}";

    /// <summary>A background that receives clicks without drawing anything.</summary>
    public static readonly Brush Clickable = Brushes.Transparent;

    /// <summary>A window point in an element's coordinates (scrolling included).</summary>
    public static Point To(UIElement element, Point window) =>
        Root is null ? window : Root.TranslatePoint(window, element);

    /// <summary>A rectangle in <paramref name="from"/>'s coordinates, in the overlay's.</summary>
    public static Rect InOverlay(UIElement from, Canvas overlay, Rect r) =>
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
    /// The width the items get: the scroller's width less a normal vertical scroll bar when one is shown. Unlike
    /// ViewportWidth it does not change while the scroll bar widens under the pointer, so scrolling never resizes
    /// the columns (calendar-views R1a).
    /// </summary>
    public static double ItemsWidth(ScrollViewer scroll) =>
        scroll.ActualWidth - (scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible ? SystemParameters.VerticalScrollBarWidth : 0);

    /// <summary>
    /// <paramref name="size"/> rounded down to whole device pixels. Layout rounding draws each strip item at a whole
    /// pixel size, so an item size that is not one would drift from positions computed as index × size (the all-day
    /// lane, the anchor day) by a little more each item.
    /// </summary>
    public static double WholePixels(double size, Visual visual)
    {
        var scale = VisualTreeHelper.GetDpi(visual).DpiScaleX;
        return Math.Floor(size * scale) / scale;
    }

    /// <summary>
    /// A strip that follows another ScrollViewer (day headers, all-day lane, hour labels): no scroll bars, no user
    /// scrolling of its own, moved by the view in the same layout pass as the main scroll.
    /// </summary>
    public static ScrollViewer FollowerStrip(bool horizontal)
    {
        var strip = new ScrollViewer
        {
            HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden,
            Focusable = false,
        };
        // The wheel scrolls the main view, not the strip.
        strip.PreviewMouseWheel += (_, e) => e.Handled = true;
        return strip;
    }

    /// <summary>A Segoe Fluent Icons glyph at <paramref name="size"/> epx.</summary>
    public static TextBlock Icon(string glyph, double size) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// A day header: weekday, then the date (in a chip for today), and an optional label such as "Tomorrow" or a
    /// holiday name (trimmed, full text in a tooltip).
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
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        stack.Children.Add(new TextBlock
        {
            Text = date.ToString("ddd"),
            Style = Theme.Style("CaptionTextBlockStyle"),
            Foreground = Theme.Brush("TextSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2),
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
            var text = Caption(label);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(text);
        }
        AutomationProperties.SetName(stack, label is null ? date.ToString("D") : $"{label}, {date:D}");
        return stack;
    }

    /// <summary>Where the window's content dialogs show (WPF-UI); set by the main window.</summary>
    public static Wpf.Ui.Controls.ContentDialogHost? DialogHost { get; set; }

    /// <summary>A form field: its label as a caption above the control (WPF controls have no Header).</summary>
    public static StackPanel Field(string header, UIElement control)
    {
        var field = new StackPanel();
        var label = new Label { Content = header, Padding = new Thickness(0, 0, 0, 4), Target = control as FrameworkElement };
        label.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        field.Children.Add(label);
        field.Children.Add(control);
        if (control is FrameworkElement element)
        {
            AutomationProperties.SetName(element, header);
        }
        return field;
    }

    /// <summary>A borderless button showing a glyph, with its name as tooltip and for screen readers.</summary>
    public static Button IconButton(string glyph, string name)
    {
        var button = new Button
        {
            Content = Icon(glyph, 14),
            Padding = new Thickness(8),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = name,
        };
        AutomationProperties.SetName(button, name);
        return button;
    }

    /// <summary>
    /// A new item's heading as a drop-down: <paramref name="current"/>, with <paramref name="other"/> in its menu to
    /// switch kind (calendar-views and tasks interim). Starts collapsed.
    /// </summary>
    public static Button KindSwitch(string current, string other, Action onSwitch, out MenuItem otherItem)
    {
        var item = new MenuItem { Header = other };
        item.Click += (_, _) => onSwitch();
        var menu = new ContextMenu();
        menu.Items.Add(item);
        otherItem = item;
        var chevron = Icon("", 10);
        chevron.Margin = new Thickness(8, 4, 0, 0);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = current, Style = Theme.Style("SubtitleTextBlockStyle") });
        content.Children.Add(chevron);
        var button = new Button
        {
            Content = content,
            ContextMenu = menu,
            Focusable = false,
            Background = Clickable,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
        };
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        AutomationProperties.SetName(button, $"{current}; switch to {other}");
        return button;
    }

    public static void Place(UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
    }

    /// <summary>A secondary caption trimmed to its space, with the full text in a tooltip.</summary>
    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = Theme.Style("CaptionTextBlockStyle"),
        Foreground = Theme.Brush("TextSecondaryBrush"),
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
        Padding = new Thickness(4, 0, 4, 0),
        ToolTip = text,
    };

    /// <summary>
    /// Day background: a holiday or a day that is not a working day the non-working shade (calendar-shading R2,
    /// R4, R8); otherwise today a faint blue. A holiday wins over today.
    /// </summary>
    public static Brush? DayShade(DateOnly date, bool isToday, bool isHoliday = false) =>
        isHoliday ? Theme.Brush("ShadeNonWorkingBrush")
        : isToday ? Theme.Brush("ShadeTodayBrush")
        : !Working.IsWorkingDay(date) ? Theme.Brush("ShadeNonWorkingBrush")
        : null;
}
