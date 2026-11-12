using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Views;

/// <summary>One calendar colour's parts in one theme (ui-colour-theme R6), as RRGGBB. CardMarker is the marker on an event card's fill.</summary>
public sealed record CalendarColourSteps(string Swatch, string Fill, string Text, string CardMarker);

/// <summary>
/// The theme colour values (ui-colour-theme R3, R6), defined once here; the app's Theme and CalendarPalette build their
/// brushes from them, and ThemeContrastTests checks them. High contrast uses system colours instead (R11).
/// </summary>
public static class ThemeColours
{
    /// <summary>Colour roles (R3) by resource key: (light, dark) as #RRGGBB.</summary>
    public static readonly IReadOnlyDictionary<string, (string Light, string Dark)> Roles = new Dictionary<string, (string, string)>
    {
        ["SurfaceBaseBrush"] = ("#FFFFFF", "#121824"),
        ["SurfaceRaisedBrush"] = ("#FFFFFF", "#222D46"),
        ["DividerBrush"] = ("#D2D4D9", "#2E3F61"),
        ["ShadeTodayBrush"] = ("#F4F7FE", "#0F1E3C"),
        ["ShadeNonWorkingBrush"] = ("#FBF6F8", "#201928"),
        ["TextPrimaryBrush"] = ("#16181A", "#EBEFF5"),
        ["TextSecondaryBrush"] = ("#4E535C", "#A8B7D5"),
        ["TodayChipBrush"] = ("#155CEE", "#6E9BF4"),
        ["TodayChipTextBrush"] = ("#FFFFFF", "#121824"),
        ["NowLineBrush"] = ("#CD1E1E", "#EC7A7A"),
        ["DestructiveBrush"] = ("#CD1E1E", "#EC7A7A"),
        ["DestructiveHoverBrush"] = ("#A31717", "#F1A1A1"),
        ["DestructivePressedBrush"] = ("#801212", "#E54B4B"),
        ["TextOnDestructiveBrush"] = ("#FFFFFF", "#121824"),
    };

    // Ramp index (step / 10 - 1) per level 1..3. Light: swatch 50/70/90, fill 10/20/30. Dark: swatch 20/40/50, fill 90/80/70.
    // The marker on a card is the swatch, except dark level 3, whose swatch (50) is too close to its fill (70): step 20
    // there (ui-colour-theme interim).
    private static readonly int[] LightSwatch = [4, 6, 8];
    private static readonly int[] LightFill = [0, 1, 2];
    private static readonly int[] DarkSwatch = [1, 3, 4];
    private static readonly int[] DarkFill = [8, 7, 6];
    private static readonly int[] DarkCardMarker = [1, 3, 1];

    /// <summary>
    /// The parts of a calendar colour stored as "Hue/Level" (e.g. "Navy/L1"); an unknown hue is Navy and an unknown
    /// level is L1. Event text is Grey 100 (light) or Night 10 (dark).
    /// </summary>
    public static CalendarColourSteps Calendar(string colour, bool dark)
    {
        var parts = colour.Split('/');
        var ramp = CalendarColours.Ramps.TryGetValue(parts[0], out var r) ? r : CalendarColours.Ramps["Navy"];
        var level = parts.Length > 1 && int.TryParse(parts[1].TrimStart('L'), out var l) ? Math.Clamp(l, 1, 3) - 1 : 0;
        var swatch = ramp[(dark ? DarkSwatch : LightSwatch)[level]];
        return new CalendarColourSteps(
            swatch,
            ramp[(dark ? DarkFill : LightFill)[level]],
            (dark ? Roles["TextPrimaryBrush"].Dark : Roles["TextPrimaryBrush"].Light).TrimStart('#'),
            dark ? ramp[DarkCardMarker[level]] : swatch);
    }
}
