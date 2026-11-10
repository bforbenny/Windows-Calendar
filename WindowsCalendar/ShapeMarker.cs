using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// Calendar markers (ui-colour-theme R7): a 10 epx vector shape, or a Fluent icon ("Glyph:XXXX"), in the calendar's
/// swatch colour.
/// </summary>
internal static class ShapeMarker
{
    public const double Size = 10;

    public const string GlyphPrefix = "Glyph:";

    /// <summary>The shapes, in picker order.</summary>
    public static readonly (string Shape, string Name)[] Shapes =
    [
        ("Circle", "Circle"), ("Square", "Square"), ("UpTriangle", "Up triangle"), ("DownTriangle", "Down triangle"), ("Pentagon", "Pentagon"),
    ];

    /// <summary>The Fluent icons offered in the picker (Segoe Fluent Icons code points).</summary>
    public static readonly (string Code, string Name)[] Icons =
    [
        ("E80F", "Home"), ("E821", "Work"), ("E716", "People"), ("E77B", "Person"), ("EB51", "Heart"), ("E734", "Star"),
        ("E7C1", "Flag"), ("E718", "Pin"), ("E787", "Calendar"), ("E823", "Clock"), ("E709", "Airplane"), ("E804", "Car"),
        ("E806", "Bus"), ("E7C0", "Train"), ("E805", "Walk"), ("E7BE", "Education"), ("E8F1", "Books"), ("E95E", "Health"),
        ("E717", "Phone"), ("E715", "Mail"), ("E8BD", "Message"), ("E774", "Globe"), ("E707", "Map pin"), ("E719", "Shopping"),
        ("E7BF", "Cart"), ("E8D6", "Music"), ("E722", "Camera"), ("E7FC", "Game"), ("E706", "Sun"), ("E708", "Moon"),
        ("E753", "Cloud"), ("E945", "Lightning"), ("E90F", "Repair"), ("E713", "Settings"), ("E8B7", "Folder"), ("E8A5", "Document"),
        ("E8EF", "Calculator"), ("E76E", "Smile"), ("E8E1", "Like"), ("E73E", "Check"), ("EA18", "Shield"), ("E946", "Info"),
    ];

    /// <summary>The marker for <paramref name="shape"/>; <paramref name="size"/> is larger in the pickers.</summary>
    public static FrameworkElement Create(string shape, Brush fill, double size = Size)
    {
        if (shape.StartsWith(GlyphPrefix, StringComparison.Ordinal)
            && int.TryParse(shape.AsSpan(GlyphPrefix.Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
        {
            // A glyph's ink is smaller than its em box: 1.2 x the shape size looks the same weight.
            var icon = ViewParts.Icon(char.ConvertFromUtf32(code), size * 1.2);
            icon.Foreground = fill;
            return icon;
        }
        Shape marker = shape switch
        {
            "Square" => new Rectangle(),
            "UpTriangle" => Polygon(new(5, 0), new(10, 10), new(0, 10)),
            "DownTriangle" => Polygon(new(0, 0), new(10, 0), new(5, 10)),
            "Pentagon" => Polygon(new(5, 0), new(10, 3.8), new(8.1, 10), new(1.9, 10), new(0, 3.8)),
            _ => new Ellipse(),
        };
        marker.Width = size;
        marker.Height = size;
        marker.Stretch = Stretch.Fill;
        marker.Fill = fill;
        marker.VerticalAlignment = VerticalAlignment.Center;
        return marker;
    }

    private static Polygon Polygon(params Point[] points)
    {
        var polygon = new Polygon();
        foreach (var p in points)
        {
            polygon.Points.Add(p);
        }
        return polygon;
    }
}
