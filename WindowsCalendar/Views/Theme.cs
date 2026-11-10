using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WindowsCalendar.Views;

/// <summary>The app's light or dark theme (app-platform R2a); high contrast is separate (<see cref="Theme.HighContrast"/>).</summary>
internal enum AppTheme { Light, Dark }

/// <summary>
/// Colour-role brushes (ui-colour-theme R3) for the current theme. <see cref="Apply"/> puts them in the application
/// resources, so XAML using DynamicResource and code reading <see cref="Brush"/> both follow theme changes.
/// </summary>
internal static class Theme
{
    // Colour roles: (light, dark). High contrast uses system colours (see HighContrastRoles).
    private static readonly Dictionary<string, (string Light, string Dark)> Roles = new()
    {
        ["SurfaceBaseBrush"] = ("#FFFFFF", "#121824"),
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

    private static readonly Dictionary<string, Brush> HighContrastRoles = new()
    {
        ["SurfaceBaseBrush"] = SystemColors.WindowBrush,
        ["DividerBrush"] = SystemColors.WindowTextBrush,
        ["ShadeTodayBrush"] = SystemColors.WindowBrush,
        ["ShadeNonWorkingBrush"] = SystemColors.WindowBrush,
        ["TextPrimaryBrush"] = SystemColors.WindowTextBrush,
        ["TextSecondaryBrush"] = SystemColors.WindowTextBrush,
        ["TodayChipBrush"] = SystemColors.HighlightBrush,
        ["TodayChipTextBrush"] = SystemColors.HighlightTextBrush,
        ["NowLineBrush"] = SystemColors.HighlightBrush,
        ["DestructiveBrush"] = SystemColors.HighlightBrush,
        ["DestructiveHoverBrush"] = SystemColors.HighlightBrush,
        ["DestructivePressedBrush"] = SystemColors.HighlightBrush,
        ["TextOnDestructiveBrush"] = SystemColors.HighlightTextBrush,
    };

    /// <summary>The current theme; set by <see cref="Apply"/>.</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static bool HighContrast => SystemParameters.HighContrast;

    /// <summary>The Windows app mode (Settings > Personalisation > Colours).</summary>
    public static AppTheme SystemTheme()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0 ? AppTheme.Dark : AppTheme.Light;
    }

    /// <summary>
    /// Switches the Fluent controls and the colour roles to <paramref name="theme"/>. The app's accent (the Today
    /// chip colour) replaces the Windows accent inside the app (ui-colour-theme R5).
    /// </summary>
    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var fluent = HighContrast ? ApplicationTheme.HighContrast : theme == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(fluent, WindowBackdropType.Mica, updateAccent: false);
        var resources = Application.Current.Resources;
        foreach (var (key, (light, dark)) in Roles)
        {
            resources[key] = HighContrast ? HighContrastRoles[key] : Solid(theme == AppTheme.Dark ? dark : light);
        }
        if (!HighContrast)
        {
            var accent = (Color)ColorConverter.ConvertFromString(theme == AppTheme.Dark ? Roles["TodayChipBrush"].Dark : Roles["TodayChipBrush"].Light);
            ApplicationAccentColorManager.Apply(accent, fluent);
        }
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static CalendarBrushes Calendar(string colour) => CalendarPalette.For(colour, Current, HighContrast);

    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    private static SolidColorBrush Solid(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
