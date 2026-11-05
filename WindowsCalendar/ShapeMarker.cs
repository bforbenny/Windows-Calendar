using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace WindowsCalendar;

/// <summary>
/// Calendar shape markers (ui-colour-theme R7): 10 epx, vector-drawn, filled in the calendar's swatch colour.
/// </summary>
internal static class ShapeMarker
{
    public const double Size = 10;

    public static Shape Create(string shape, Brush fill)
    {
        Shape marker = shape switch
        {
            "Square" => new Rectangle(),
            "UpTriangle" => Polygon(new(5, 0), new(10, 10), new(0, 10)),
            "DownTriangle" => Polygon(new(0, 0), new(10, 0), new(5, 10)),
            "Pentagon" => Polygon(new(5, 0), new(10, 3.8), new(8.1, 10), new(1.9, 10), new(0, 3.8)),
            _ => new Ellipse(),
        };
        marker.Width = Size;
        marker.Height = Size;
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
