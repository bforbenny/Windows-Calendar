namespace WindowsCalendar.Core.Store;

/// <summary>
/// Automatic colour and shape for a new calendar (ui-colour-theme R8). Colours are stored as "Hue/Level",
/// e.g. "Navy/L1"; adjacent calendars differ in hue, lightness and shape.
/// </summary>
public static class CollectionStyle
{
    private static readonly string[] Hues = ["Navy", "Orange", "Teal", "Pink", "Sea Green", "Purple", "Amber", "Strawberry Red"];
    private static readonly string[] Shapes = ["Circle", "Square", "UpTriangle", "DownTriangle", "Pentagon"];

    /// <summary>Style for calendar number <paramref name="index"/> (0-based, in order added).</summary>
    public static (string Colour, string Shape) For(int index) =>
        ($"{Hues[index % Hues.Length]}/L{index % 3 + 1}", Shapes[index % Shapes.Length]);

    /// <summary>
    /// Style for calendar number <paramref name="index"/>, moving on to later numbers while that
    /// colour and shape combination is already in use.
    /// </summary>
    public static (string Colour, string Shape) Next(int index, IReadOnlySet<(string Colour, string Shape)> inUse)
    {
        // 8 x 3 x 5 = 120 distinct combinations; after that, reuse is unavoidable.
        for (var i = index; i < index + 120; i++)
        {
            var style = For(i);
            if (!inUse.Contains(style))
            {
                return style;
            }
        }
        return For(index);
    }
}
