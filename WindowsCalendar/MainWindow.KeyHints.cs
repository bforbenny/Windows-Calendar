using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// Shortcut badges (keyboard-navigation interim): holding Ctrl for half a second shows each control's key under it;
/// releasing Ctrl, pressing another key or leaving the window hides them.
/// </summary>
public sealed partial class MainWindow
{
    private readonly DispatcherTimer _keyHintTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private void SetupKeyHints()
    {
        _keyHintTimer.Tick += (_, _) =>
        {
            _keyHintTimer.Stop();
            ShowKeyHints();
        };
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (IsCtrl(e.Key))
            {
                // Key repeat while held does not restart the wait.
                if (!e.IsRepeat)
                {
                    _keyHintTimer.Start();
                }
            }
            else
            {
                HideKeyHints();
            }
        }), handledEventsToo: true);
        AddHandler(PreviewKeyUpEvent, new KeyEventHandler((_, e) =>
        {
            if (IsCtrl(e.Key))
            {
                HideKeyHints();
            }
        }), handledEventsToo: true);
        Deactivated += (_, _) => HideKeyHints();
    }

    private static bool IsCtrl(Key key) => key is Key.LeftCtrl or Key.RightCtrl;

    private void ShowKeyHints()
    {
        KeyHintLayer.Children.Clear();
        AddKeyHint(SidebarButton, "Ctrl+B");
        AddKeyHint(NewButton, "Ctrl+N");
        AddKeyHint(TodayButton, "T");
        AddKeyHint(PeriodTitle, "Ctrl+G");
        for (var i = 0; i < ViewSwitcher.Children.Count && i < 4; i++)
        {
            AddKeyHint((FrameworkElement)ViewSwitcher.Children[i], $"F{i + 1}");
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
        _keyHintTimer.Stop();
        KeyHintLayer.Children.Clear();
    }

    /// <summary>A badge just under <paramref name="target"/>'s left edge, if it is on screen.</summary>
    private void AddKeyHint(FrameworkElement target, string keys)
    {
        if (target.ActualWidth <= 0 || !target.IsVisible)
        {
            return;
        }
        var at = target.TransformToVisual(KeyHintLayer).Transform(new Point(0, target.ActualHeight + 2));
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
}
