using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// Shortcut badges (keyboard-navigation interim): holding Ctrl for half a second shows each control's key under it;
/// releasing Ctrl, pressing another key or leaving the window hides them.
/// </summary>
public sealed partial class MainWindow
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _keyHintTimer;

    private void SetupKeyHints()
    {
        _keyHintTimer = DispatcherQueue.CreateTimer();
        _keyHintTimer.Interval = TimeSpan.FromMilliseconds(500);
        _keyHintTimer.IsRepeating = false;
        _keyHintTimer.Tick += (_, _) => ShowKeyHints();

        var content = (UIElement)Content;
        content.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (IsCtrl(e.Key))
            {
                // Key repeat while held does not restart the wait.
                if (!e.KeyStatus.WasKeyDown)
                {
                    _keyHintTimer.Start();
                }
            }
            else
            {
                HideKeyHints();
            }
        }), handledEventsToo: true);
        content.AddHandler(UIElement.PreviewKeyUpEvent, new KeyEventHandler((_, e) =>
        {
            if (IsCtrl(e.Key))
            {
                HideKeyHints();
            }
        }), handledEventsToo: true);
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                HideKeyHints();
            }
        };
    }

    private static bool IsCtrl(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl;

    private void ShowKeyHints()
    {
        KeyHintLayer.Children.Clear();
        AddKeyHint(SidebarButton, "Ctrl+B");
        AddKeyHint(NewButton, "Ctrl+N");
        AddKeyHint(TodayButton, "T");
        AddKeyHint(PeriodTitle, "Ctrl+G");
        for (var i = 0; i < ViewSwitcher.Items.Count && i < 4; i++)
        {
            AddKeyHint(ViewSwitcher.Items[i], $"F{i + 1}");
        }
        AddKeyHint(SearchBox, "Ctrl+F");
        AddKeyHint(SyncButton, "F5");
        AddKeyHint(SettingsButton, "Ctrl+,");
        if (UndoBar.IsOpen)
        {
            AddKeyHint(UndoButton, "Ctrl+Z");
        }
    }

    private void HideKeyHints()
    {
        _keyHintTimer?.Stop();
        KeyHintLayer.Children.Clear();
    }

    /// <summary>A badge just under <paramref name="target"/>'s left edge, if it is on screen.</summary>
    private void AddKeyHint(FrameworkElement target, string keys)
    {
        if (target.ActualWidth <= 0 || !IsShown(target))
        {
            return;
        }
        var at = target.TransformToVisual(KeyHintLayer).TransformPoint(new Windows.Foundation.Point(0, target.ActualHeight + 2));
        var badge = new Border
        {
            Background = Theme.Brush("TextPrimaryBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 2),
            Child = new TextBlock { Text = keys, Style = Theme.Style("CaptionTextBlockStyle"), Foreground = Theme.Brush("SurfaceBaseBrush") },
        };
        ViewParts.Place(badge, at.X, at.Y);
        KeyHintLayer.Children.Add(badge);
    }

    /// <summary>Whether the element and all its parents are visible (e.g. not under a collapsed toolbar).</summary>
    private static bool IsShown(FrameworkElement element)
    {
        for (DependencyObject? e = element; e is not null; e = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(e))
        {
            if (e is UIElement { Visibility: Visibility.Collapsed })
            {
                return false;
            }
        }
        return true;
    }
}
