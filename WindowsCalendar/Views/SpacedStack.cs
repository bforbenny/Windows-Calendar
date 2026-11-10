using System;
using System.Windows;
using System.Windows.Controls;

namespace WindowsCalendar.Views;

/// <summary>
/// A stack with a gap between visible children (WPF's StackPanel has no Spacing). Collapsed children take no gap.
/// </summary>
internal class SpacedStack : Panel
{
    public Orientation Orientation { get; set; } = Orientation.Vertical;

    public double Spacing { get; set; }

    protected override Size MeasureOverride(Size available)
    {
        var horizontal = Orientation == Orientation.Horizontal;
        var slot = horizontal ? new Size(double.PositiveInfinity, available.Height) : new Size(available.Width, double.PositiveInfinity);
        double along = 0, across = 0;
        var count = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(slot);
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }
            var size = child.DesiredSize;
            along += (count++ > 0 ? Spacing : 0) + (horizontal ? size.Width : size.Height);
            across = Math.Max(across, horizontal ? size.Height : size.Width);
        }
        return horizontal ? new Size(along, across) : new Size(across, along);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var horizontal = Orientation == Orientation.Horizontal;
        double offset = 0;
        var count = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }
            offset += count++ > 0 ? Spacing : 0;
            var size = child.DesiredSize;
            child.Arrange(horizontal
                ? new Rect(offset, 0, size.Width, final.Height)
                : new Rect(0, offset, final.Width, size.Height));
            offset += horizontal ? size.Width : size.Height;
        }
        return final;
    }
}
