using System.Text.RegularExpressions;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Core.Tests;

/// <summary>
/// WCAG contrast of the colour roles and calendar colours in both themes (ui-colour-theme R3, R6; acceptance criterion 1),
/// and no hex colour outside the theme tables (criterion 2). Text needs 4.5:1, swatches, bars and lines 3:1.
/// </summary>
public sealed class ThemeContrastTests
{
    private const double Text = 4.5;
    private const double NonText = 3.0;

    private static readonly string[] Surfaces = ["SurfaceBaseBrush", "SurfaceRaisedBrush", "ShadeTodayBrush", "ShadeNonWorkingBrush"];

    private static string Role(string key, bool dark) => dark ? ThemeColours.Roles[key].Dark : ThemeColours.Roles[key].Light;

    private static string Step(string hue, int step) => CalendarColours.Ramps[hue][step / 10 - 1];

    /// <summary>
    /// R3 tokens WPF-UI draws today (ui-colour-theme interim), at their R3 palette values. Night 100 is the dark text on
    /// accent and status fills.
    /// </summary>
    private static (string Accent, string AccentHover, string AccentPressed, string OnAccent, string[] Status) Palette(bool dark) => dark
        ? (Step("Navy", 40), Step("Navy", 30), Step("Navy", 50), "121824",
            [Step("Strawberry Red", 40), Step("Amber", 40), Step("Sea Green", 40), Step("Navy", 40)])
        : (Step("Navy", 60), Step("Navy", 70), Step("Navy", 80), "FFFFFF",
            [Step("Strawberry Red", 60), Step("Amber", 60), Step("Sea Green", 60), Step("Navy", 60)]);

    public static TheoryData<bool> Themes => [false, true];

    [Theory]
    [MemberData(nameof(Themes))]
    public void TextRoles_OnEverySurface_MeetAA(bool dark)
    {
        var accent = Palette(dark).Accent;
        foreach (var surface in Surfaces)
        {
            AssertContrast(Role("TextPrimaryBrush", dark), Role(surface, dark), Text, $"Text.Primary on {surface}");
            AssertContrast(Role("TextSecondaryBrush", dark), Role(surface, dark), Text, $"Text.Secondary on {surface}");
            AssertContrast(accent, Role(surface, dark), Text, $"Accent link on {surface}");
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void TextOnFilledButtonsAndChip_MeetsAA(bool dark)
    {
        var p = Palette(dark);
        foreach (var (fill, name) in new[] { (p.Accent, "Accent"), (p.AccentHover, "Accent.Hover"), (p.AccentPressed, "Accent.Pressed") })
        {
            AssertContrast(p.OnAccent, fill, Text, $"Text.OnAccent on {name}");
        }
        foreach (var fill in new[] { "DestructiveBrush", "DestructiveHoverBrush", "DestructivePressedBrush" })
        {
            AssertContrast(Role("TextOnDestructiveBrush", dark), Role(fill, dark), Text, $"Text.OnDestructive on {fill}");
        }
        AssertContrast(Role("TodayChipTextBrush", dark), Role("TodayChipBrush", dark), Text, "Today.DateChip text");
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void LinesAndStatus_OnEverySurface_Reach3To1(bool dark)
    {
        var p = Palette(dark);
        foreach (var surface in Surfaces)
        {
            AssertContrast(Role("NowLineBrush", dark), Role(surface, dark), NonText, $"NowLine on {surface}");
            AssertContrast(Role("TextPrimaryBrush", dark), Role(surface, dark), NonText, $"Selection outline on {surface}");
            foreach (var status in p.Status)
            {
                AssertContrast(status, Role(surface, dark), NonText, $"Status {status} on {surface}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryCalendarColour_MeetsR6(bool dark)
    {
        foreach (var hue in CalendarColours.Ramps.Keys)
        {
            for (var level = 1; level <= 3; level++)
            {
                var colour = $"{hue}/L{level}";
                var steps = ThemeColours.Calendar(colour, dark);
                foreach (var surface in Surfaces)
                {
                    AssertContrast(steps.Swatch, Role(surface, dark), NonText, $"{colour} swatch on {surface}");
                }
                AssertContrast(steps.Text, steps.Fill, Text, $"{colour} event text on fill");
                AssertContrast(steps.CardMarker, steps.Fill, NonText, $"{colour} card marker on fill");
            }
        }
    }

    [Fact]
    public void Calendar_UsesTheR6Steps()
    {
        Assert.Equal(new CalendarColourSteps(Step("Teal", 70), Step("Teal", 20), "16181A", Step("Teal", 70)),
            ThemeColours.Calendar("Teal/L2", dark: false));
        Assert.Equal(new CalendarColourSteps(Step("Teal", 50), Step("Teal", 70), "EBEFF5", Step("Teal", 20)),
            ThemeColours.Calendar("Teal/L3", dark: true));
    }

    [Fact]
    public void NoHexColour_OutsideTheThemeTables()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WindowsCalendar.slnx")))
        {
            root = root.Parent;
        }
        Assert.NotNull(root);

        string[] allowed = ["ThemeColours.cs", "CalendarColours.cs"];
        var hex = new Regex(@"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\b|""[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?""");
        var found = new List<string>();
        foreach (var project in new[] { "WindowsCalendar", "WindowsCalendar.Core" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root.FullName, project), "*.*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root.FullName, file);
                var parts = relative.Split(Path.DirectorySeparatorChar);
                if (!(file.EndsWith(".cs") || file.EndsWith(".xaml")) || parts.Contains("obj") || parts.Contains("bin")
                    || allowed.Contains(Path.GetFileName(file)))
                {
                    continue;
                }
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (hex.IsMatch(lines[i]))
                    {
                        found.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                    }
                }
            }
        }
        Assert.True(found.Count == 0, "Hex colours outside the theme tables:\n" + string.Join("\n", found));
    }

    private static void AssertContrast(string foreground, string background, double minimum, string what)
    {
        var ratio = Contrast(foreground, background);
        Assert.True(ratio >= minimum, $"{what}: {foreground} on {background} is {ratio:F2}:1, needs {minimum}:1");
    }

    /// <summary>WCAG 2 contrast ratio of two #RRGGBB / RRGGBB colours.</summary>
    private static double Contrast(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        hex = hex.TrimStart('#');
        static double Channel(string h)
        {
            var s = Convert.ToInt32(h, 16) / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(hex[..2]) + 0.7152 * Channel(hex[2..4]) + 0.0722 * Channel(hex[4..6]);
    }
}
