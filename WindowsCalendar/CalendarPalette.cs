using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

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
    // Steps 10..90 per hue (ui-colour-theme R1).
    private static readonly Dictionary<string, string[]> Ramps = new()
    {
        ["Navy"] = ["E8EFFD", "C2D5FA", "97B7F7", "6E9BF4", "437CF1", "155CEE", "0E49BF", "0B3892", "08296C"],
        ["Sea Green"] = ["D1F7EB", "7BE8C7", "25CF9C", "1FB085", "1A936F", "157659", "115E47", "0D4837", "093528"],
        ["Strawberry Red"] = ["FCEAEA", "F7CACA", "F1A1A1", "EC7A7A", "E54B4B", "CD1E1E", "A31717", "801212", "5E0E0E"],
        ["Amber"] = ["FFEEBF", "FFCF4E", "EAAA00", "C99200", "A77900", "886300", "6B4E00", "523C00", "3C2B00"],
        ["Purple"] = ["F0EDF6", "DBCFF4", "C6A9FA", "B484FF", "A259FF", "9026F5", "6F2BB9", "512984", "39225A"],
        ["Teal"] = ["D0F6F5", "6DE7E6", "2ECACA", "08AEAE", "069090", "187474", "205B5B", "204545", "1C3231"],
        ["Orange"] = ["F6EDE8", "F5CCB6", "FBA16E", "FA7603", "D06101", "A65114", "7F431D", "5D351D", "412819"],
        ["Pink"] = ["F6ECF0", "F5C8DC", "FB98C8", "FF61B7", "F402A2", "C31B82", "942665", "6C244B", "4B1E36"],
    };

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
            var ramp = Ramps.TryGetValue(parts[0], out var r) ? r : Ramps["Navy"];
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
