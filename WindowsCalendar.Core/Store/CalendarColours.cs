using System.Globalization;

namespace WindowsCalendar.Core.Store;

/// <summary>
/// The 24 calendar colours (ui-colour-theme R1, R6): 8 hues, each a ramp of steps 10..90, at 3 levels stored as
/// "Hue/Level", e.g. "Navy/L1". Server colours are snapped to the nearest one (R9).
/// </summary>
public static class CalendarColours
{
    /// <summary>Steps 10..90 per hue (ui-colour-theme R1), as RRGGBB.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Ramps = new Dictionary<string, string[]>
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

    /// <summary>Ramp index of the light-theme swatch step (50 / 70 / 90) per level 1..3.</summary>
    private static readonly int[] LightSwatch = [4, 6, 8];

    /// <summary>
    /// The calendar colour nearest to a server colour (<c>#RRGGBB</c> or <c>#RRGGBBAA</c>, alpha ignored) by CIELAB
    /// ΔE (CIE76) against each colour's light swatch step; null when the text is not such a colour.
    /// </summary>
    public static string? Snap(string? serverColour)
    {
        if (Parse(serverColour) is not { } rgb)
        {
            return null;
        }
        var target = Lab(rgb);
        string? best = null;
        var bestDistance = double.MaxValue;
        foreach (var (hue, ramp) in Ramps)
        {
            for (var level = 0; level < LightSwatch.Length; level++)
            {
                var lab = Lab(Parse("#" + ramp[LightSwatch[level]])!.Value);
                var d = Math.Pow(lab.L - target.L, 2) + Math.Pow(lab.A - target.A, 2) + Math.Pow(lab.B - target.B, 2);
                if (d < bestDistance)
                {
                    (best, bestDistance) = ($"{hue}/L{level + 1}", d);
                }
            }
        }
        return best;
    }

    private static (int R, int G, int B)? Parse(string? text)
    {
        var hex = text?.Trim().TrimStart('#');
        if (hex is null || (hex.Length != 6 && hex.Length != 8)
            || !int.TryParse(hex[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }
        return ((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
    }

    /// <summary>sRGB (D65) to CIELAB.</summary>
    private static (double L, double A, double B) Lab((int R, int G, int B) c)
    {
        static double Linear(int v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;

        double r = Linear(c.R), g = Linear(c.G), b = Linear(c.B);
        var x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047;
        var y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        var z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }
}
