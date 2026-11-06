using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar;

/// <summary>Brushes for one calendar colour in the current theme.</summary>
internal sealed record CalendarBrushes(Brush Swatch, Brush Fill, Brush Text);

/// <summary>
/// Calendar colours (ui-colour-theme R1, R6, R11). A colour is stored as "Hue/Level", e.g. "Navy/L1".
/// Light: swatch/bar step 50/70/90, fill 10/20/30, text Grey 100. Dark: swatch 20/40/60, fill 90/80/70, text Night 10.
/// High contrast: system colours only; calendars are told apart by name and shape.
/// </summary>
internal static class CalendarPalette
{
    // Ramp index (step / 10 - 1) per level 1..3.
    private static readonly int[] LightSwatch = [4, 6, 8];
    private static readonly int[] LightFill = [0, 1, 2];
    private static readonly int[] DarkSwatch = [1, 3, 5];
    private static readonly int[] DarkFill = [8, 7, 6];

    private static readonly Dictionary<(string, ElementTheme, bool), CalendarBrushes> Cache = [];

    public static CalendarBrushes For(string colour, ElementTheme theme, bool highContrast)
    {
        if (Cache.TryGetValue((colour, theme, highContrast), out var cached))
        {
            return cached;
        }

        CalendarBrushes brushes;
        if (highContrast)
        {
            brushes = new CalendarBrushes(
                SystemBrush("SystemColorHighlightColor"), SystemBrush("SystemColorWindowColor"), SystemBrush("SystemColorWindowTextColor"));
        }
        else
        {
            var parts = colour.Split('/');
            var ramp = CalendarColours.Ramps.TryGetValue(parts[0], out var r) ? r : CalendarColours.Ramps["Navy"];
            var level = parts.Length > 1 && int.TryParse(parts[1].TrimStart('L'), out var l) ? Math.Clamp(l, 1, 3) - 1 : 0;
            var dark = theme == ElementTheme.Dark;
            brushes = new CalendarBrushes(
                Brush(ramp[(dark ? DarkSwatch : LightSwatch)[level]]),
                Brush(ramp[(dark ? DarkFill : LightFill)[level]]),
                Brush(dark ? "EBEFF5" : "16181A"));
        }
        Cache[(colour, theme, highContrast)] = brushes;
        return brushes;
    }

    private static SolidColorBrush Brush(string hex) => new(Color.FromArgb(0xFF,
        Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16)));

    private static SolidColorBrush SystemBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Color c ? new SolidColorBrush(c) : new SolidColorBrush(Colors.Gray);
}
