using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Views;

/// <summary>
/// The clash and short-gap indicators in the views (schedule-gap-warning R11): in Week an outline on each card involved
/// and a warning icon where the problem starts; in Month and Agenda a warning icon beside the date. Caution colour for
/// an overlap, attention colour for a short gap; every icon has a tooltip and screen-reader name (ui-colour-theme R4).
/// </summary>
internal static class ProblemMarks
{
    private const string WarningGlyph = "";

    /// <summary>The problems among the shown events, set by the main window on each render; empty when the set's switch is off.</summary>
    public static IReadOnlyList<ScheduleProblem> All { get; set; } = [];

    /// <summary>The problems that start on <paramref name="date"/> in the Windows timezone.</summary>
    public static List<ScheduleProblem> On(DateOnly date) =>
        [.. All.Where(p => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(p.Start, TimeZoneInfo.Local).DateTime) == date)];

    /// <summary>Month and Agenda: the icon beside the date, or null when the day has no problem.</summary>
    public static FrameworkElement? DayIcon(DateOnly date)
    {
        var problems = On(date);
        if (problems.Count == 0)
        {
            return null;
        }
        var overlap = problems.Any(p => p.Overlap);
        var text = ScheduleCheck.Summary(problems);
        var icon = new TextBlock
        {
            Text = WarningGlyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = text,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, BrushKey(overlap));
        AutomationProperties.SetName(icon, text);
        return icon;
    }

    /// <summary>
    /// Week: the hazard outline (alternating yellow and red dashes) for a card involved in any problem, placed by the
    /// caller in the card's slot, or null. The same for an overlap and a short gap; the icon tells them apart. It
    /// takes no clicks.
    /// </summary>
    public static FrameworkElement? Outline(string key)
    {
        if (!All.Any(p => p.First == key || p.Second == key))
        {
            return null;
        }
        var outline = new Grid { Margin = new Thickness(1, 0, 1, 0), IsHitTestVisible = false };
        foreach (var (brush, dashed) in new[] { ("SystemFillColorCautionBrush", false), ("SystemFillColorCriticalBrush", true) })
        {
            var stroke = new System.Windows.Shapes.Rectangle { StrokeThickness = 2, RadiusX = 4, RadiusY = 4 };
            if (dashed)
            {
                stroke.StrokeDashArray = new DoubleCollection { 3, 3 };
            }
            stroke.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brush);
            outline.Children.Add(stroke);
        }
        return outline;
    }

    /// <summary>
    /// Month and Agenda: the same hazard outline drawn inside <paramref name="card"/>, out to its edges, when its
    /// occurrence is in a problem. The card keeps its size, so the layout does not move.
    /// </summary>
    public static void Decorate(Border card, string key)
    {
        if (Outline(key) is not { } outline)
        {
            return;
        }
        var (p, b) = (card.Padding, card.BorderThickness);
        outline.Margin = new Thickness(-p.Left - b.Left, -p.Top - b.Top, -p.Right - b.Right, -p.Bottom - b.Bottom);
        var layers = new Grid();
        var content = card.Child;
        card.Child = null;
        if (content is not null)
        {
            layers.Children.Add(content);
        }
        layers.Children.Add(outline);
        card.Child = layers;
    }

    /// <summary>Week: a warning icon at the day column's right edge where each problem on <paramref name="date"/> starts.</summary>
    public static void AddIcons(Panel column, DateOnly date)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        foreach (var p in On(date))
        {
            var start = TimeZoneInfo.ConvertTime(p.Start, TimeZoneInfo.Local).DateTime;
            var top = DayColumnPanel.PixelsFor(Math.Max(0, (start - dayStart).TotalMinutes));
            var icon = new TextBlock
            {
                Text = WarningGlyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                // Centred on the problem's start, clear of the column's top edge.
                Margin = new Thickness(0, Math.Max(0, top - 6), 1, 0),
                Background = Brushes.Transparent,
                ToolTip = p.Text,
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, BrushKey(p.Overlap));
            AutomationProperties.SetName(icon, p.Text);
            // A click on the icon shows its tooltip only; it does not create an event in the column below.
            icon.MouseLeftButtonUp += (_, e) => e.Handled = true;
            column.Children.Add(icon);
        }
    }

    private static string BrushKey(bool overlap) => overlap ? "SystemFillColorCautionBrush" : "SystemFillColorAttentionBrush";
}
