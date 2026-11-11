using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace WindowsCalendar.Views;

/// <summary>
/// A scrolling list of fixed-size items (Month's weeks, Week's and Agenda's days) built in code (calendar-views R1a).
/// Only items near the viewport exist; one that scrolls away is reused for one coming in (WPF virtualization in
/// recycling mode, scrolled by pixel). While <see cref="KeepAll"/> is set, nothing is taken away, so a dragged card
/// keeps its mouse capture. The items are the indices 0 .. count - 1.
/// </summary>
internal sealed class VirtualStrip : ItemsControl
{
    private readonly Func<FrameworkElement> _create;
    private readonly Action<FrameworkElement, int> _bind;
    private readonly Dictionary<FrameworkElement, int> _realized = [];

    public VirtualStrip(Orientation orientation, bool scrollBars, Func<FrameworkElement> create, Action<FrameworkElement, int> bind)
    {
        (_create, _bind) = (create, bind);
        var panel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
        panel.SetValue(VirtualizingStackPanel.OrientationProperty, orientation);
        ItemsPanel = new ItemsPanelTemplate(panel);

        var scroll = new FrameworkElementFactory(typeof(ScrollViewer), "Scroll");
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroll.SetValue(FocusableProperty, false);
        var bars = scrollBars ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,
            orientation == Orientation.Vertical ? ScrollBarVisibility.Disabled : bars);
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, bars);
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        Template = new ControlTemplate(typeof(ItemsControl)) { VisualTree = scroll };

        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
        VirtualizingPanel.SetCacheLengthUnit(this, VirtualizationCacheLengthUnit.Page);
        VirtualizingPanel.SetCacheLength(this, new VirtualizationCacheLength(1));
        VirtualizingStackPanel.AddCleanUpVirtualizedItemHandler(this, (_, e) => e.Cancel = KeepAll);
        Focusable = false;
        ApplyTemplate();
        Scroll = (ScrollViewer)Template.FindName("Scroll", this);
        if (!scrollBars)
        {
            // A follower: the wheel scrolls the main view, not this strip.
            Scroll.PreviewMouseWheel += (_, e) => e.Handled = true;
        }
        else if (orientation == Orientation.Horizontal)
        {
            // WPF raises no event for the horizontal (tilt) wheel, so take WM_MOUSEHWHEEL from the window.
            HwndSource? source = null;
            Loaded += (_, _) =>
            {
                source = PresentationSource.FromVisual(this) as HwndSource;
                source?.AddHook(TiltWheel);
            };
            Unloaded += (_, _) => source?.RemoveHook(TiltWheel);
        }
    }

    private IntPtr TiltWheel(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEHWHEEL = 0x020E;
        // Over the whole view (headers included), not just the body.
        if (msg == WM_MOUSEHWHEEL && (Parent as UIElement ?? this).IsMouseOver)
        {
            // High word of wParam: 120 per notch, positive = right. 48 px per notch, as the vertical wheel.
            var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            Scroll.ScrollToHorizontalOffset(Scroll.HorizontalOffset + delta * 48 / 120.0);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>The strip's scroller (offsets in pixels).</summary>
    public ScrollViewer Scroll { get; }

    /// <summary>Set during a drag: items out of view are kept.</summary>
    public bool KeepAll { get; set; }

    /// <summary>Shows items 0 .. <paramref name="count"/> - 1; existing items are rebound when the count is unchanged.</summary>
    public void SetCount(int count)
    {
        if (ItemsSource is List<int> list && list.Count == count)
        {
            RebindAll();
            return;
        }
        ItemsSource = Enumerable.Range(0, count).ToList();
    }

    /// <summary>
    /// Shows items 0 .. <paramref name="count"/> - 1 from scratch, after the item size changed: the panel forgets the
    /// sizes it measured, so two strips that follow each other map an offset to the same item again.
    /// </summary>
    public void Reset(int count) => ItemsSource = Enumerable.Range(0, count).ToList();

    /// <summary>Rebuilds the content of every item that exists now (new data, size or theme).</summary>
    public void RebindAll()
    {
        foreach (var (element, index) in _realized.ToList())
        {
            _bind(element, index);
        }
    }

    protected override bool IsItemItsOwnContainerOverride(object item) => false;

    protected override DependencyObject GetContainerForItemOverride() => _create();

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        var container = (FrameworkElement)element;
        _realized[container] = (int)item;
        _bind(container, (int)item);
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        _realized.Remove((FrameworkElement)element);
        base.ClearContainerForItemOverride(element, item);
    }
}
