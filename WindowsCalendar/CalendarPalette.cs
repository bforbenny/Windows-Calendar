using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using WindowsCalendar.Core.Views;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>Brushes for one calendar colour in the current theme. CardMarker is the marker on an event card's fill.</summary>
internal sealed record CalendarBrushes(Brush Swatch, Brush Fill, Brush Text, Brush CardMarker);

/// <summary>
/// Calendar colours (ui-colour-theme R1, R6, R11). A colour is stored as "Hue/Level", e.g. "Navy/L1".
/// The steps per theme and level are in <see cref="ThemeColours.Calendar"/>.
/// High contrast: system colours only; calendars are told apart by name and shape.
/// </summary>
internal static class CalendarPalette
{
    private static readonly Dictionary<(string, AppTheme, bool), CalendarBrushes> Cache = [];

    public static CalendarBrushes For(string colour, AppTheme theme, bool highContrast)
    {
        if (Cache.TryGetValue((colour, theme, highContrast), out var cached))
        {
            return cached;
        }

        CalendarBrushes brushes;
        if (highContrast)
        {
            var highlight = SystemColors.HighlightBrush;
            brushes = new CalendarBrushes(highlight, SystemColors.WindowBrush, SystemColors.WindowTextBrush, highlight);
        }
        else
        {
            var steps = ThemeColours.Calendar(colour, theme == AppTheme.Dark);
            var swatch = Brush(steps.Swatch);
            brushes = new CalendarBrushes(swatch, Brush(steps.Fill), Brush(steps.Text),
                steps.CardMarker == steps.Swatch ? swatch : Brush(steps.CardMarker));
        }
        Cache[(colour, theme, highContrast)] = brushes;
        return brushes;
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush(Color.FromArgb(0xFF,
            Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16)));
        brush.Freeze();
        return brush;
    }
}
