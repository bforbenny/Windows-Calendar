using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// Search (search first slice): the toolbar box finds events and tasks in the local store as you type (R2), lists
/// them under the box (R3, R6, R7), and choosing one goes to its day and opens it (R5).
/// </summary>
public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _searchTimer;

    private void FocusSearch()
    {
        SearchBox.Focus(FocusState.Keyboard);
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }
        if (_searchTimer is null)
        {
            _searchTimer = DispatcherQueue.CreateTimer();
            _searchTimer.Interval = TimeSpan.FromMilliseconds(150);
            _searchTimer.IsRepeating = false;
            _searchTimer.Tick += (_, _) => ShowSearchResults();
        }
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ShowSearchResults()
    {
        var text = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(text) || Store is not { } store)
        {
            SearchBox.ItemsSource = null;
            return;
        }
        var rows = new List<FrameworkElement>();
        foreach (var result in store.Search(text))
        {
            if (_collections.TryGetValue(result.CollectionId, out var collection))
            {
                rows.Add(ResultRow(result, collection));
            }
        }
        if (rows.Count == 0)
        {
            rows.Add(NoteRow("No results"));
        }
        if (_graphAccounts.Count > 0)
        {
            rows.Add(NoteRow($"Microsoft 365 searched from {GraphSync.WindowStart(_today):d MMM yyyy}"));
        }
        SearchBox.ItemsSource = rows;
        SearchBox.IsSuggestionListOpen = true;
    }

    /// <summary>Enter or a click: the chosen result, or the first one when Enter is pressed in the box.</summary>
    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var chosen = args.ChosenSuggestion as FrameworkElement
            ?? (sender.ItemsSource as IEnumerable<FrameworkElement>)?.FirstOrDefault();
        if (chosen?.Tag is SearchResult result)
        {
            sender.IsSuggestionListOpen = false;
            OpenSearchResult(result);
        }
    }

    /// <summary>R5: scrolls the calendar to the result's day (Tasks switches to Week for an event) and opens it.</summary>
    private void OpenSearchResult(SearchResult result)
    {
        var stayInTasks = result.Kind == ItemKind.Task && _view == ViewKind.Tasks;
        if (result.StartUtc is { } start && !stayInTasks)
        {
            var local = start.ToLocalTime().DateTime;
            var day = DateOnly.FromDateTime(local);
            if (_view == ViewKind.Tasks)
            {
                _pendingDay = day;
                ViewSwitcher.SelectedItem = ViewSwitcher.Items[0];
            }
            else
            {
                CurrentView.ScrollTo(day, toNow: false);
            }
            CurrentView.Reveal(day, result.AllDay ? null : local);
        }
        if (result.Kind == ItemKind.Task)
        {
            OpenTask(result.ItemId);
            return;
        }
        ShowEventEditor();
        _editor.OpenExisting(result.ItemId, result.RecurrenceUtc, _collections);
    }

    /// <summary>R3: type icon, title, date and time, calendar marker and name, and the snippet; matches in bold.</summary>
    private static FrameworkElement ResultRow(SearchResult result, Collection collection)
    {
        var title = Marked(result.Title, Theme.Style("BodyStrongTextBlockStyle"));
        if (result.IsSeries)
        {
            title.Inlines.Add(new Run { Text = "  ", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 12 });
        }
        var details = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        details.Children.Add(Caption(When(result)));
        details.Children.Add(ShapeMarker.Create(collection.Shape, Theme.Calendar(collection.Colour).Swatch));
        details.Children.Add(Caption(collection.Name));
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(title);
        text.Children.Add(details);
        if (result.Snippet is { } snippet)
        {
            var line = Marked(snippet.ReplaceLineEndings(" "), Theme.Style("CaptionTextBlockStyle"));
            line.Foreground = Theme.Brush("TextSecondaryBrush");
            text.Children.Add(line);
        }
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 4, 0, 4), Tag = result, MaxWidth = 420 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon
        {
            Glyph = result.Kind == ItemKind.Task ? "" : "",
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row,
            $"{(result.Kind == ItemKind.Task ? "Task" : "Event")} {Plain(result.Title)}, {When(result)}, {collection.Name}");
        return row;
    }

    /// <summary>A line in the list that is not a result (no results, the MS365 window); choosing it does nothing.</summary>
    private static FrameworkElement NoteRow(string text)
    {
        var note = Caption(text);
        note.Margin = new Thickness(0, 4, 0, 4);
        return note;
    }

    private static string When(SearchResult result)
    {
        if (result.StartUtc is not { } start)
        {
            return "No date";
        }
        var local = start.ToLocalTime();
        var day = local.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture);
        return result.AllDay ? day : $"{day}, {local.ToString("t", CultureInfo.CurrentCulture)}";
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = Theme.Style("CaptionTextBlockStyle"),
        Foreground = Theme.Brush("TextSecondaryBrush"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Text with the store's match marks turned into bold runs, on one trimmed line.</summary>
    private static TextBlock Marked(string marked, Style style)
    {
        var block = new TextBlock { Style = style, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        var bold = false;
        foreach (var part in marked.Split(SearchResult.MarkStart, SearchResult.MarkEnd))
        {
            if (part.Length > 0)
            {
                block.Inlines.Add(new Run { Text = part, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal });
            }
            bold = !bold;
        }
        return block;
    }

    private static string Plain(string marked) =>
        marked.Replace(SearchResult.MarkStart.ToString(), "").Replace(SearchResult.MarkEnd.ToString(), "");
}
