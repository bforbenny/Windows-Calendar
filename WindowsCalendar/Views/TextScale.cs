using System;
using System.Collections.Generic;
using System.Windows;
using Serilog;
using Windows.UI.ViewManagement;

namespace WindowsCalendar.Views;

/// <summary>
/// Windows "Text size" (display-scaling R8): WPF does not follow it, so the app reads UISettings.TextScaleFactor and
/// scales the type-ramp resources (App.xaml) and WPF-UI's control text size, live when it changes. Rows that hold
/// text (Month chips, day headers, the all-day lane) use <see cref="Of"/>; timed event cards keep their time-based height.
/// </summary>
internal static class TextScale
{
    // Kept alive so the change event keeps firing.
    private static readonly UISettings Settings = new();
    private static readonly Dictionary<string, double> Base = [];
    private static readonly string[] Keys = ["CaptionFontSize", "CaptionLineHeight", "BodyFontSize", "BodyLineHeight",
        "SubtitleFontSize", "SubtitleLineHeight", "TitleFontSize", "TitleLineHeight", "ControlContentThemeFontSize"];

    /// <summary>The Windows text scale, 1 to 2.25.</summary>
    public static double Factor { get; private set; } = 1;

    /// <summary>Raised on the UI thread after the resources were rescaled; the views redraw.</summary>
    public static event Action? Changed;

    /// <summary>Reads the resource sizes once and applies the current factor; call after the app resources load.</summary>
    public static void Start()
    {
        var resources = Application.Current.Resources;
        foreach (var key in Keys)
        {
            if (resources[key] is double size)
            {
                Base[key] = size;
            }
        }
        Apply();
        Settings.TextScaleFactorChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Apply();
            Changed?.Invoke();
        });
    }

    /// <summary>A text row's height of <paramref name="epx"/> at 100% text, scaled and rounded to whole epx.</summary>
    public static double Of(double epx) => Math.Round(epx * Factor);

    private static void Apply()
    {
        try
        {
            Factor = Math.Clamp(Settings.TextScaleFactor, 1, 2.25);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reading the Windows text size failed; using 100%");
            Factor = 1;
        }
        var resources = Application.Current.Resources;
        foreach (var (key, size) in Base)
        {
            resources[key] = size * Factor;
        }
        Log.Information("Text scale {Factor:P0}", Factor);
    }
}
