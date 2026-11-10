using System;
using System.Windows;
using System.Windows.Controls;

namespace WindowsCalendar.Views;

/// <summary>
/// Places timed events in one day column: vertical position from minutes since midnight, horizontal position from
/// the side-by-side column (calendar-views R4). Height is 24 hours at <see cref="HourHeight"/>.
/// </summary>
internal sealed class DayColumnPanel : Panel
{
    /// <summary>Hour height in epx (display-scaling R5).</summary>
    public const double HourHeight = 64;

    public static readonly DependencyProperty StartMinuteProperty =
        DependencyProperty.RegisterAttached("StartMinute", typeof(double), typeof(DayColumnPanel), new PropertyMetadata(0.0));
    public static readonly DependencyProperty EndMinuteProperty =
        DependencyProperty.RegisterAttached("EndMinute", typeof(double), typeof(DayColumnPanel), new PropertyMetadata(0.0));
    public static readonly DependencyProperty ColumnProperty =
        DependencyProperty.RegisterAttached("Column", typeof(int), typeof(DayColumnPanel), new PropertyMetadata(0));
    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.RegisterAttached("Columns", typeof(int), typeof(DayColumnPanel), new PropertyMetadata(1));

    public static double PixelsFor(double minutes) => minutes / 60 * HourHeight;

    public static void Place(UIElement e, double startMinute, double endMinute, int column, int columns)
    {
        e.SetValue(StartMinuteProperty, startMinute);
        e.SetValue(EndMinuteProperty, endMinute);
        e.SetValue(ColumnProperty, column);
        e.SetValue(ColumnsProperty, Math.Max(1, columns));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 100 : availableSize.Width;
        foreach (UIElement child in Children)
        {
            var slot = Slot(child, width);
            child.Measure(new Size(slot.Width, slot.Height));
        }
        return new Size(width, PixelsFor(24 * 60));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in Children)
        {
            var (left, top, w, h) = Slot(child, finalSize.Width);
            child.Arrange(new Rect(left, top, w, h));
        }
        return finalSize;
    }

    private static (double Left, double Top, double Width, double Height) Slot(UIElement child, double width)
    {
        var start = (double)child.GetValue(StartMinuteProperty);
        var end = (double)child.GetValue(EndMinuteProperty);
        var column = (int)child.GetValue(ColumnProperty);
        var columns = (int)child.GetValue(ColumnsProperty);
        var columnWidth = Math.Max(0, (width - 4) / columns);
        var top = PixelsFor(start);
        var height = Math.Max(1, PixelsFor(end) - top);
        return (column * columnWidth, top, columnWidth, height);
    }
}
