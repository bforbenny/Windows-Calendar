using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Views;

/// <summary>
/// An occurrence in local wall-clock time, ready to draw (calendar-views). End is exclusive.
/// All-day and 24-hour-or-longer events go in the all-day lane (R3). Editable events can be dragged (R7); read-only
/// calendars, subscriptions and MS365 meetings (sync-ms365 second slice) cannot. <paramref name="Recurrence"/> names the occurrence of a repeating event.
/// A task (tasks R6 interim) is never dragged; <paramref name="Checkable"/> lets its checkbox complete it.
/// <paramref name="ShowAs"/> sets the card's style (event-show-as R5). <paramref name="Join"/> is its online-meeting link
/// (online-meeting-join R2). <paramref name="OriginalTime"/> is its time in its own timezone when that differs from
/// the Windows one (timezone-handling R9).
/// </summary>
internal sealed record DisplayEvent(string ItemId, string Title, string? Location, DateTime Start, DateTime End, bool AllDay,
    Collection Collection, bool Editable, DateTimeOffset? Recurrence, ShowAs ShowAs = ShowAs.Busy, bool IsTask = false, bool Checkable = false,
    JoinLink? Join = null, string? OriginalTime = null)
{
    /// <summary>Join shows on cards from this many minutes before the start until the end (online-meeting-join R4).</summary>
    public const int JoinLeadMinutes = 10;

    /// <summary>Whether the event is in progress or starts within <see cref="JoinLeadMinutes"/>, and has a join link.</summary>
    public bool JoinNow(DateTime now) => Join is not null && !IsTask && now >= Start.AddMinutes(-JoinLeadMinutes) && now < End;

    public bool InAllDayLane => AllDay || End - Start >= TimeSpan.FromHours(24);

    /// <summary>An event in a calendar the app can write to may be copied, meetings too (duplicate-event R1, R4).</summary>
    public bool CanDuplicate => !IsTask && !Collection.ReadOnly && Collection.SourceUrl is null;

    /// <summary>Identifies the item and occurrence, for the keyboard selection.</summary>
    public string Key => $"{ItemId}|{Recurrence?.UtcTicks}";

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
        o.ShowAs,
        Join: o.Kind == ItemKind.Event ? JoinLink.Recognise(o.JoinUrl) : null,
        OriginalTime: o.Kind == ItemKind.Event && !o.AllDay
            ? TimeZoneLabels.OriginalTime(o.StartUtc, o.EndUtc, o.TimeZone, null, TimeZoneInfo.Local) : null);

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
        string.Join(", ", new[] { Title, TimeText(), OriginalTime, Location, Collection.Name }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>
/// An event card: left bar and shape marker before the title (ui-colour-theme R6, R7), styled by show-as
/// (event-show-as R5): Busy has the calendar fill; Free is the surface with a 1 epx border; Tentative the surface with
/// a hatched bar and a dashed border; Out of office the fill with an "away" glyph; Working elsewhere the surface with
/// a dotted border and a "building" glyph. Hatching stays in the bar, never behind text. An unanswered invitation is
/// drawn by its show-as like any other event.
/// </summary>
internal static class EventCard
{
    /// <summary>The selected item's key (keyboard-navigation interim): its card gets an accent outline.</summary>
    public static string? SelectedKey { get; set; }

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
        var surface = e.ShowAs is ShowAs.Free or ShowAs.Tentative or ShowAs.WorkingElsewhere;
        var card = new Border
        {
            Background = surface ? Theme.Brush("SurfaceBaseBrush") : brushes.Fill,
            BorderBrush = e.ShowAs == ShowAs.Tentative ? Hatch(brushes.Swatch) : brushes.Swatch,
            BorderThickness = e.ShowAs == ShowAs.Free ? new Thickness(3, 1, 1, 1) : new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1, 4, 1),
            Height = height,
            Margin = new Thickness(1, 0, 1, 0),
        };
        if (e.Key == SelectedKey)
        {
            card.BorderBrush = Theme.Brush("TodayChipBrush");
            card.BorderThickness = new Thickness(3, 2, 2, 2);
            AutomationProperties.SetItemStatus(card, "Selected");
        }
        card.ToolTip = e.Description();
        AutomationProperties.SetName(card, e.Description());

        if (!chip && height < TextThreshold)
        {
            return Finish(card, null, e, brushes.Swatch);
        }

        details ??= string.Join(" · ", new[] { e.TimeText(), e.Location }.Where(t => !string.IsNullOrWhiteSpace(t)));
        var fit = double.IsNaN(height);
        var twoLines = !chip && (fit || height >= TwoLineThreshold);

        var line = Caption(brushes, fit);
        if (prefix is not null)
        {
            line.Inlines.Add(new Run { Text = prefix + " " });
        }
        if (e.ShowAs switch { ShowAs.OutOfOffice => "\uE709", ShowAs.WorkingElsewhere => "\uEC06", _ => null } is { } glyph)
        {
            line.Inlines.Add(new Run { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons") });
            line.Inlines.Add(new Run { Text = " " });
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
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // On a surface card the marker sits on the surface, not the fill.
        var markerBrush = surface ? brushes.Swatch : brushes.CardMarker;
        FrameworkElement marker = e.IsTask ? TaskBox(e, markerBrush) : ShapeMarker.Create(e.Collection.Shape, markerBrush);
        marker.Margin = new Thickness(marker.Margin.Left, marker.Margin.Top, marker.Margin.Right + 4, marker.Margin.Bottom);
        if (fit)
        {
            // Beside the first line of a wrapped title.
            card.Padding = new Thickness(4, 3, 4, 3);
            marker.VerticalAlignment = VerticalAlignment.Top;
            marker.Margin = new Thickness(marker.Margin.Left, 3, marker.Margin.Right, marker.Margin.Bottom);
        }
        row.Children.Add(marker);
        Grid.SetColumn(line, 1);
        row.Children.Add(line);
        // Day, Week and Agenda cards offer Join around the meeting's time (online-meeting-join R4).
        if (!chip && (fit || height >= TimeThreshold) && e.JoinNow(DateTime.Now))
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var join = JoinButton(e);
            join.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(join, 2);
            row.Children.Add(join);
        }

        if (!twoLines)
        {
            row.VerticalAlignment = chip ? VerticalAlignment.Center : VerticalAlignment.Top;
            return Finish(card, row, e, brushes.Swatch);
        }

        var second = Caption(brushes, fit);
        second.Text = details;
        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(second);
        return Finish(card, stack, e, brushes.Swatch);
    }

    /// <summary>
    /// Sets the card's content; Tentative and Working elsewhere get their dashed or dotted 1 epx border on top, except
    /// when selected (the selection outline replaces it).
    /// </summary>
    private static Border Finish(Border card, UIElement? content, DisplayEvent e, Brush swatch)
    {
        if (e.ShowAs is not (ShowAs.Tentative or ShowAs.WorkingElsewhere) || e.Key == SelectedKey)
        {
            card.Child = content;
            return card;
        }
        var p = card.Padding;
        var layers = new Grid();
        layers.Children.Add(new Rectangle
        {
            Stroke = swatch,
            StrokeThickness = 1,
            StrokeDashArray = e.ShowAs == ShowAs.Tentative ? new DoubleCollection { 3, 2 } : new DoubleCollection { 1, 2 },
            RadiusX = 4,
            RadiusY = 4,
            // Out to the card's edge; the left side lies under the bar.
            Margin = new Thickness(-p.Left - card.BorderThickness.Left, -p.Top, -p.Right, -p.Bottom),
            IsHitTestVisible = false,
        });
        if (content is not null)
        {
            layers.Children.Add(content);
        }
        card.Child = layers;
        return card;
    }

    /// <summary>Diagonal stripes of the calendar colour for a Tentative card's bar.</summary>
    private static Brush Hatch(Brush swatch)
    {
        if (swatch is not SolidColorBrush solid)
        {
            return swatch;
        }
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(4, 4),
            SpreadMethod = GradientSpreadMethod.Repeat,
        };
        brush.GradientStops.Add(new GradientStop { Color = solid.Color, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = solid.Color, Offset = 0.5 });
        brush.GradientStops.Add(new GradientStop { Color = Colors.Transparent, Offset = 0.5 });
        brush.GradientStops.Add(new GradientStop { Color = Colors.Transparent, Offset = 1 });
        return brush;
    }

    /// <summary>A small Join button; pressing it neither opens nor drags the card.</summary>
    private static Wpf.Ui.Controls.Button JoinButton(DisplayEvent e)
    {
        var link = e.Join!;
        var button = new Wpf.Ui.Controls.Button
        {
            Content = "Join",
            // Filled in the accent colour, so it stands out on the tinted card (high contrast keeps its own colours).
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            FontSize = 12,
            Padding = new Thickness(6, 0, 6, 1),
            MinHeight = 0,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = $"Join {link.Provider} meeting (Ctrl+J when selected)",
        };
        AutomationProperties.SetName(button, $"Join {link.Provider} meeting: {e.Title}");
        // The button handles the mouse itself, so pressing it neither opens nor drags the card.
        button.Click += (_, _) => MeetingJoin.Launch(link);
        return button;
    }

    /// <summary>An empty checkbox in the list colour before a task's title; ticking it completes the task.</summary>
    private static FrameworkElement TaskBox(DisplayEvent e, Brush colour)
    {
        var box = ViewParts.Icon("\uE739", 12);
        box.Foreground = colour;
        if (e.Checkable)
        {
            // A larger hit area than the glyph, so a click near it completes rather than opens.
            var target = new Border
            {
                Background = ViewParts.Clickable, Child = box, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(-4, -2, -4, -2),
                ToolTip = "Mark complete",
            };
            AutomationProperties.SetName(target, $"Mark complete: {e.Title}");
            // Mouse events handled, so the card neither opens nor starts a drag.
            target.MouseLeftButtonDown += (_, args) => args.Handled = true;
            target.MouseLeftButtonUp += (_, args) =>
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
        Style = Theme.Style("CaptionTextBlockStyle"),
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
