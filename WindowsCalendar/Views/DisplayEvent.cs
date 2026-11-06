using System;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Views;

/// <summary>
/// An occurrence in local wall-clock time, ready to draw (calendar-views). End is exclusive.
/// All-day and 24-hour-or-longer events go in the all-day lane (R3). Editable events can be dragged (R7); read-only
/// calendars, subscriptions and MS365 meetings (sync-ms365 second slice) cannot. <paramref name="Recurrence"/> names the occurrence of a repeating event.
/// A task (tasks R6 interim) is never dragged; <paramref name="Checkable"/> lets its checkbox complete it.
/// </summary>
internal sealed record DisplayEvent(string ItemId, string Title, string? Location, DateTime Start, DateTime End, bool AllDay,
    Collection Collection, bool Editable, DateTimeOffset? Recurrence, bool Muted = false, bool IsTask = false, bool Checkable = false)
{
    public bool InAllDayLane => AllDay || End - Start >= TimeSpan.FromHours(24);

    public DateOnly FirstDay => DateOnly.FromDateTime(Start);

    /// <summary>Last day touched (inclusive); an event ending at midnight does not touch the next day.</summary>
    public DateOnly LastDay => End > Start ? DateOnly.FromDateTime(End.AddTicks(-1)) : FirstDay;

    public static DisplayEvent From(OccurrenceView o, Collection collection) => new(
        o.ItemId,
        string.IsNullOrWhiteSpace(o.Summary) ? "(No title)" : o.Summary,
        o.Location,
        TimeZoneInfo.ConvertTime(o.StartUtc, TimeZoneInfo.Local).DateTime,
        TimeZoneInfo.ConvertTime(o.EndUtc, TimeZoneInfo.Local).DateTime,
        o.AllDay,
        collection,
        !o.ReadOnly && !collection.ReadOnly && collection.SourceUrl is null,
        o.RecurrenceUtc,
        o.Unaccepted);

    /// <summary>Times in the Windows region format (ui-style R7).</summary>
    public string TimeText()
    {
        if (IsTask)
        {
            return AllDay ? "Due" : $"Due {Start:t}";
        }
        if (AllDay)
        {
            return LastDay == FirstDay ? "All day" : $"{FirstDay:d} – {LastDay:d}";
        }
        return FirstDay == LastDay ? $"{Start:t} – {End:t}" : $"{Start:g} – {End:g}";
    }

    /// <summary>Tooltip and screen-reader name (calendar-views R4a, keyboard-navigation R6).</summary>
    public string Description() =>
        string.Join(", ", new[] { Title, TimeText(), Location, Collection.Name }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>
/// An event card: calendar fill, left bar, shape marker before the title (ui-colour-theme R6, R7). An invitation not
/// accepted is an outline instead: surface background with a 1 epx border in the calendar colour (attendees-invitations interim).
/// </summary>
internal static class EventCard
{
    /// <summary>Raised when a task card's checkbox is ticked (tasks R6 interim).</summary>
    public static event Action<DisplayEvent>? TaskChecked;

    /// <summary>Below this height a timed card shows only its bar and fill (calendar-views R4a).</summary>
    public const double TextThreshold = 12;

    /// <summary>From this height a timed card adds the time after the title.</summary>
    public const double TimeThreshold = 24;

    /// <summary>From this height a timed card puts the time and location on a second line.</summary>
    public const double TwoLineThreshold = 44;

    /// <summary>
    /// A card of the given height. Timed cards in Day/Week (<paramref name="chip"/> false): no text below 12 epx,
    /// then the title only, then "Title  09:00 – 09:30" on one line, then title over "time · location". Chips (Month, all-day lane)
    /// always show one line: <paramref name="prefix"/> (e.g. the start time) and the title.
    /// <paramref name="details"/> replaces the second line (e.g. Agenda's "Day 2 of 3").
    /// A <paramref name="height"/> of NaN fits the content (Agenda R4a): title wrapped over as many lines as needed,
    /// then the second line.
    /// </summary>
    public static Border Create(DisplayEvent e, CalendarBrushes brushes, double height, bool chip = false, string? prefix = null,
        string? details = null)
    {
        var card = new Border
        {
            Background = e.Muted ? Theme.Brush("SurfaceBaseBrush") : brushes.Fill,
            BorderBrush = brushes.Swatch,
            BorderThickness = e.Muted ? new Thickness(3, 1, 1, 1) : new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1, 4, 1),
            Height = height,
            Margin = new Thickness(1, 0, 1, 0),
        };
        ToolTipService.SetToolTip(card, e.Description());
        AutomationProperties.SetName(card, e.Description());

        if (!chip && height < TextThreshold)
        {
            return card;
        }

        details ??= string.Join(" · ", new[] { e.TimeText(), e.Location }.Where(t => !string.IsNullOrWhiteSpace(t)));
        var fit = double.IsNaN(height);
        var twoLines = !chip && (fit || height >= TwoLineThreshold);

        var line = Caption(brushes, fit);
        if (prefix is not null)
        {
            line.Inlines.Add(new Run { Text = prefix + " " });
        }
        line.Inlines.Add(new Run { Text = e.Title, FontWeight = FontWeights.SemiBold });
        if (!chip && !twoLines && height >= TimeThreshold)
        {
            line.Inlines.Add(new Run { Text = "  " + e.TimeText() });
        }
        if (!chip && height < TimeThreshold)
        {
            // Fit one line into a 15-minute card: no vertical padding, line height = text size.
            card.Padding = new Thickness(4, 0, 4, 0);
            line.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            line.LineHeight = Math.Max(1, height - 1);
        }

        // Grid, not a horizontal StackPanel, so the text gets a width and trims with an ellipsis.
        var row = new Grid { ColumnSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // On a muted (outline) card the marker sits on the surface, not the fill.
        var markerBrush = e.Muted ? brushes.Swatch : brushes.CardMarker;
        FrameworkElement marker = e.IsTask ? TaskBox(e, markerBrush) : ShapeMarker.Create(e.Collection.Shape, markerBrush);
        if (fit)
        {
            // Beside the first line of a wrapped title.
            card.Padding = new Thickness(4, 3, 4, 3);
            marker.VerticalAlignment = VerticalAlignment.Top;
            marker.Margin = new Thickness(0, 3, 0, 0);
        }
        row.Children.Add(marker);
        Grid.SetColumn(line, 1);
        row.Children.Add(line);

        if (!twoLines)
        {
            row.VerticalAlignment = chip ? VerticalAlignment.Center : VerticalAlignment.Top;
            card.Child = row;
            return card;
        }

        var second = Caption(brushes, fit);
        second.Text = details;
        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(second);
        card.Child = stack;
        return card;
    }

    /// <summary>An empty checkbox in the list colour before a task's title; ticking it completes the task.</summary>
    private static FrameworkElement TaskBox(DisplayEvent e, Brush colour)
    {
        var box = new FontIcon { Glyph = "", FontSize = 12, Foreground = colour, VerticalAlignment = VerticalAlignment.Center };
        if (e.Checkable)
        {
            // A larger hit area than the glyph, so a click near it completes rather than opens.
            var target = new Border { Background = ViewParts.Clickable, Child = box, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(-4, -2, -4, -2) };
            ToolTipService.SetToolTip(target, "Mark complete");
            AutomationProperties.SetName(target, $"Mark complete: {e.Title}");
            // Handled, so the card does not open as well.
            // Pointer events handled too, so the card neither opens nor starts a drag.
            target.PointerPressed += (_, args) => args.Handled = true;
            target.PointerReleased += (_, args) => args.Handled = true;
            target.Tapped += (_, args) =>
            {
                args.Handled = true;
                TaskChecked?.Invoke(e);
            };
            return target;
        }
        return box;
    }

    private static TextBlock Caption(CalendarBrushes brushes, bool wrap) => new()
    {
        Foreground = brushes.Text,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        TextWrapping = wrap ? TextWrapping.WrapWholeWords : TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
