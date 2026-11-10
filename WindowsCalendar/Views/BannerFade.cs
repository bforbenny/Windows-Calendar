using System;
using System.Windows;
using System.Windows.Media.Animation;
using InfoBar = Wpf.Ui.Controls.InfoBar;

namespace WindowsCalendar.Views;

/// <summary>
/// Opens and closes a banner with a short fade (floating-banners R7); no fade when Windows animation effects are off
/// (ui-style R6). The banner's own close button closes it at once.
/// </summary>
internal static class BannerFade
{
    private static readonly Duration Length = TimeSpan.FromMilliseconds(150);

    public static void SetOpen(InfoBar bar, bool open)
    {
        if (open == bar.IsOpen && !bar.HasAnimatedProperties)
        {
            return;
        }
        if (!SystemParameters.ClientAreaAnimation)
        {
            bar.BeginAnimation(UIElement.OpacityProperty, null);
            bar.Opacity = 1;
            bar.IsOpen = open;
            return;
        }
        if (open)
        {
            var wasOpen = bar.IsOpen;
            bar.IsOpen = true;
            bar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(wasOpen ? bar.Opacity : 0, 1, Length));
            return;
        }
        var fade = new DoubleAnimation(bar.Opacity, 0, Length);
        fade.Completed += (_, _) =>
        {
            // A show during the fade replaced this animation; leave the banner open.
            if (bar.Opacity == 0)
            {
                bar.IsOpen = false;
                bar.BeginAnimation(UIElement.OpacityProperty, null);
                bar.Opacity = 1;
            }
        };
        bar.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
