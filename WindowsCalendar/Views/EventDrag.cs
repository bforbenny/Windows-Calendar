using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace WindowsCalendar.Views;

/// <summary>What a drag does: move the event, or move its start or end edge (calendar-views R7).</summary>
internal enum DragMode { Move, ResizeStart, ResizeEnd }

/// <summary>
/// Where the event would land, and the preview rectangle in the view's overlay. <paramref name="Copy"/> marks a Ctrl+drag,
/// which copies the event there instead of moving it (duplicate-event R1).
/// </summary>
internal sealed record DragTarget(DateTime Start, DateTime End, bool AllDay, Rect Ghost, bool Copy = false);

/// <summary>The view side of a drag: hit-testing in its own layout, auto-scroll and the result.</summary>
internal interface IDragHost
{
    /// <summary>Canvas over the whole view for the preview; not hit-testable.</summary>
    Canvas Overlay { get; }

    /// <summary>Remembers where the event was grabbed (window coordinates).</summary>
    void BeginDrag(DisplayEvent e, DragMode mode, Point window);

    /// <summary>The landing place for the pointer, or null when it is outside the view.</summary>
    DragTarget? Target(DisplayEvent e, DragMode mode, Point window);

    /// <summary>Scrolls a little when the pointer is near the edge of the view.</summary>
    void AutoScroll(Point window);

    void EndDrag();

    void CompleteDrag(DisplayEvent e, DragTarget target);

    void OpenEvent(DisplayEvent e);
}

/// <summary>An edge of a timed card in Week that resizes it; shows the up-down cursor.</summary>
internal sealed class ResizeHandle : Grid
{
    public ResizeHandle(DragMode mode)
    {
        Mode = mode;
        Height = 6;
        Background = ViewParts.Clickable;
        VerticalAlignment = mode == DragMode.ResizeStart ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        Cursor = Cursors.SizeNS;
    }

    public DragMode Mode { get; }
}

/// <summary>
/// Click-or-drag on an event card (calendar-views R6b, R7). Mouse and pen: under 4 epx of travel it opens the event;
/// more starts a drag with a snapped preview, Esc cancels. Touch keeps scrolling and opens on tap. Cards that are not
/// editable only open. Ctrl+drag copies the event, editable or not, and right-click offers Duplicate (duplicate-event R1).
/// </summary>
internal sealed class EventDrag
{
    private const double Threshold = 4;

    private readonly FrameworkElement _card;
    private readonly DisplayEvent _event;
    private readonly IDragHost _host;
    private readonly DispatcherTimer _scrollTimer;
    private Point _press;
    private Point _last;
    private DragMode _mode;
    private bool _down;
    private bool _pressed;
    private bool _dragging;
    private bool _copy;
    private Border? _ghost;

    private EventDrag(FrameworkElement card, DisplayEvent e, IDragHost host)
    {
        (_card, _event, _host) = (card, e, host);
        _scrollTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _scrollTimer.Tick += (_, _) =>
        {
            _host.AutoScroll(_last);
            UpdateGhost();
        };

        card.MouseLeftButtonDown += OnPressed;
        card.MouseMove += OnMoved;
        card.MouseLeftButtonUp += OnReleased;
        card.LostMouseCapture += (_, _) => Cancel();
        if (e.CanDuplicate)
        {
            var duplicate = new MenuItem { Header = "Duplicate", InputGestureText = "Ctrl+D" };
            duplicate.Click += (_, _) => DuplicateRequested?.Invoke(_event);
            card.ContextMenu = new ContextMenu { Items = { duplicate } };
        }
    }

    /// <summary>Raised by a card's Duplicate menu item.</summary>
    public static event Action<DisplayEvent>? DuplicateRequested;

    /// <summary>Raised when a drag ends, dropped or cancelled.</summary>
    public static event Action? Ended;

    /// <summary>The drag in progress, if any (one at a time).</summary>
    public static EventDrag? Active { get; private set; }

    /// <summary>Makes a card open on click and, if editable, draggable.</summary>
    public static void Attach(FrameworkElement card, DisplayEvent e, IDragHost host) => _ = new EventDrag(card, e, host);

    /// <summary>Esc: cancels the drag in progress. Returns false when there is none.</summary>
    public static bool CancelActive()
    {
        if (Active is not { } drag)
        {
            return false;
        }
        drag.Cancel();
        return true;
    }

    private static Point Window(MouseEventArgs args) => ViewParts.Root is { } root ? args.GetPosition(root) : args.GetPosition(null);

    private void OnPressed(object sender, MouseButtonEventArgs args)
    {
        args.Handled = true;
        _down = true;
        _copy = _event.CanDuplicate && Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var touch = args.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Touch;
        if (!(_event.Editable || _copy) || touch)
        {
            return;
        }
        _mode = !_copy && args.OriginalSource is DependencyObject source && FindHandle(source) is { } handle ? handle.Mode : DragMode.Move;
        _press = _last = Window(args);
        _pressed = _card.CaptureMouse();
    }

    private ResizeHandle? FindHandle(DependencyObject? element)
    {
        while (element is not null && element != _card)
        {
            if (element is ResizeHandle handle)
            {
                return handle;
            }
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void OnMoved(object sender, MouseEventArgs args)
    {
        if (!_pressed)
        {
            return;
        }
        _last = Window(args);
        if (!_dragging && Math.Abs(_last.X - _press.X) + Math.Abs(_last.Y - _press.Y) > Threshold)
        {
            Start();
        }
        if (_dragging)
        {
            UpdateGhost();
            args.Handled = true;
        }
    }

    private void OnReleased(object sender, MouseButtonEventArgs args)
    {
        if (!_down)
        {
            return;
        }
        args.Handled = true;
        _down = false;
        if (!_dragging)
        {
            _pressed = false;
            _card.ReleaseMouseCapture();
            _host.OpenEvent(_event);
            return;
        }
        var target = _host.Target(_event, _mode, Window(args));
        Finish();
        if (target is not null && (target.Start != _event.Start || target.End != _event.End || target.AllDay != _event.AllDay))
        {
            _host.CompleteDrag(_event, target with { Copy = _copy });
        }
    }

    private void Start()
    {
        _dragging = true;
        Active = this;
        _host.BeginDrag(_event, _mode, _press);
        var brushes = Theme.Calendar(_event.Collection.Colour);
        _ghost = new Border
        {
            Background = brushes.Fill,
            BorderBrush = brushes.Swatch,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Opacity = 0.85,
            Child = new TextBlock
            {
                Foreground = brushes.Text,
                Style = Theme.Style("CaptionTextBlockStyle"),
                Margin = new Thickness(6, 2, 6, 2),
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        _host.Overlay.Children.Add(_ghost);
        _card.Opacity = _copy ? 1 : 0.4;
        _scrollTimer.Start();
        UpdateGhost();
    }

    private void UpdateGhost()
    {
        if (_ghost is null)
        {
            return;
        }
        if (_host.Target(_event, _mode, _last) is not { } target)
        {
            _ghost.Visibility = Visibility.Collapsed;
            return;
        }
        _ghost.Visibility = Visibility.Visible;
        Canvas.SetLeft(_ghost, target.Ghost.X);
        Canvas.SetTop(_ghost, target.Ghost.Y);
        _ghost.Width = Math.Max(8, target.Ghost.Width);
        _ghost.Height = Math.Max(8, target.Ghost.Height);
        ((TextBlock)_ghost.Child).Text = target.AllDay
            ? $"{_event.Title}  {DateOnly.FromDateTime(target.Start):d}"
            : $"{_event.Title}  {target.Start:t} – {target.End:t}";
    }

    private void Cancel()
    {
        if (_dragging)
        {
            Finish();
        }
        _pressed = false;
        _down = false;
    }

    private void Finish()
    {
        _scrollTimer.Stop();
        if (_ghost is not null)
        {
            _host.Overlay.Children.Remove(_ghost);
            _ghost = null;
        }
        _card.Opacity = 1;
        var wasDragging = _dragging;
        _dragging = false;
        _pressed = false;
        if (Active == this)
        {
            Active = null;
        }
        if (_card.IsMouseCaptured)
        {
            _card.ReleaseMouseCapture();
        }
        if (wasDragging)
        {
            _host.EndDrag();
            Ended?.Invoke();
        }
    }
}
