using System.Globalization;

namespace WindowsCalendar.Core.Views;

/// <summary>A rectangle in physical screen pixels (Win32 RECT order).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public bool Contains(PixelRect r) => r.Left >= Left && r.Top >= Top && r.Right <= Right && r.Bottom <= Bottom;
}

/// <summary>
/// The main window's saved placement and the fit-to-screen maths (display-scaling R3): the restored rectangle in
/// physical pixels plus the maximised state, saved as text in the local store.
/// </summary>
public static class WindowFit
{
    /// <summary>"max" or "normal", then the restored rectangle: "normal;100,80,1500,980".</summary>
    public static string Format(bool maximised, PixelRect normal) =>
        string.Create(CultureInfo.InvariantCulture, $"{(maximised ? "max" : "normal")};{normal.Left},{normal.Top},{normal.Right},{normal.Bottom}");

    /// <summary>The saved placement, or null when the text is missing or malformed.</summary>
    public static (bool Maximised, PixelRect Normal)? Parse(string? text)
    {
        var parts = text?.Split(';');
        if (parts is not { Length: 2 } || parts[0] is not ("max" or "normal"))
        {
            return null;
        }
        var n = parts[1].Split(',');
        if (n.Length != 4 || !n.All(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            return null;
        }
        var v = n.Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var rect = new PixelRect(v[0], v[1], v[2], v[3]);
        return rect.Width > 0 && rect.Height > 0 ? (parts[0] == "max", rect) : null;
    }

    /// <summary>
    /// <paramref name="window"/> moved and, if needed, shrunk to lie inside <paramref name="work"/>; kept where it is
    /// when it already fits. With <paramref name="centre"/> it is centred on the work area instead.
    /// </summary>
    public static PixelRect Fit(PixelRect window, PixelRect work, bool centre)
    {
        var w = Math.Min(window.Width, work.Width);
        var h = Math.Min(window.Height, work.Height);
        var x = centre ? work.Left + (work.Width - w) / 2 : Math.Clamp(window.Left, work.Left, work.Right - w);
        var y = centre ? work.Top + (work.Height - h) / 2 : Math.Clamp(window.Top, work.Top, work.Bottom - h);
        return new PixelRect(x, y, x + w, y + h);
    }
}
