using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace WindowsCalendar.Views;

/// <summary>
/// Colour-role brushes (ui-colour-theme R3) for the window's current theme. Views are built in code, so they read
/// the theme dictionary directly; Application.Resources alone would keep the theme the app started with.
/// </summary>
internal static class Theme
{
    private static readonly AccessibilitySettings Accessibility = new();

    /// <summary>The window's actual theme; set by MainWindow before each render.</summary>
    public static ElementTheme Current { get; set; } = ElementTheme.Light;

    public static bool HighContrast => Accessibility.HighContrast;

    public static Brush Brush(string key)
    {
        var name = HighContrast ? "HighContrast" : Current == ElementTheme.Dark ? "Dark" : "Light";
        return (Brush)((ResourceDictionary)Application.Current.Resources.ThemeDictionaries[name])[key];
    }

    public static CalendarBrushes Calendar(string colour) => CalendarPalette.For(colour, Current, HighContrast);

    public static Style Style(string key) => (Style)Application.Current.Resources[key];
}
