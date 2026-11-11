using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// Search (search first slice): the toolbar box finds events and tasks in the local store as you type (R2), lists
/// them under the box (R3, R6, R7), and choosing one goes to its day and opens it (R5). Down moves into the list;
/// Enter in the box opens the first result; Esc closes the list.
/// </summary>
public sealed partial class MainWindow
{
    private DispatcherTimer? _searchTimer;

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!SearchBox.IsKeyboardFocusWithin)
        {
            return;
        }
        if (_searchTimer is null)
        {
            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _searchTimer.Tick += (_, _) =>
            {
                _searchTimer.Stop();
                ShowSearchResults();
            };
        }
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ShowSearchResults()
    {
        var text = SearchBox.Text;
        SearchResults.Items.Clear();
        if (string.IsNullOrWhiteSpace(text) || Store is not { } store)
        {
            SearchPopup.IsOpen = false;
            return;
        }
        foreach (var result in store.Search(text))
        {
            if (_collections.TryGetValue(result.CollectionId, out var collection))
            {
                SearchResults.Items.Add(new ListBoxItem { Content = ResultRow(result, collection), Tag = result });
            }
        }
        if (SearchResults.Items.Count == 0)
        {
            SearchResults.Items.Add(NoteRow("No results"));
        }
        if (_graphAccounts.Count > 0)
        {
            SearchResults.Items.Add(NoteRow($"Microsoft 365 searched from {GraphSync.WindowStart(_today):d MMM yyyy}"));
        }
        SearchPopup.IsOpen = true;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (SearchResults.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is SearchResult) is { } first)
                {
                    OpenSearchResult(first);
                }
                break;
            case Key.Down when SearchPopup.IsOpen:
                e.Handled = true;
                if (SearchResults.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is SearchResult) is { } top)
                {
                    SearchResults.SelectedItem = top;
                    top.Focus();
                }
                break;
            case Key.Escape when SearchPopup.IsOpen:
                e.Handled = true;
                SearchPopup.IsOpen = false;
                break;
        }
    }

    private void OnSearchResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchResults.SelectedItem is ListBoxItem { Tag: SearchResult } item)
        {
            e.Handled = true;
            OpenSearchResult(item);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SearchPopup.IsOpen = false;
            SearchBox.Focus();
        }
    }

    private void OnSearchResultClicked(object sender, MouseButtonEventArgs e)
    {
        // A Run in a result row is not a Visual, so walk its logical parent instead.
        for (var hit = e.OriginalSource as DependencyObject; hit is not null;
            hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit))
        {
            if (hit is ListBoxItem { Tag: SearchResult } item)
            {
                OpenSearchResult(item);
                return;
            }
        }
    }

    /// <summary>R5: scrolls the calendar to the result's day (Tasks switches to Week for an event) and opens it.</summary>
    private void OpenSearchResult(ListBoxItem item)
    {
        SearchPopup.IsOpen = false;
        OpenResult((SearchResult)item.Tag);
    }

    /// <summary>
    /// A reminder toast's Open (reminders-notifications R3) or a tray flyout item (tray-flyout R4): the occurrence's
    /// day, with its editor open.
    /// </summary>
    internal void OpenFromReminder(string itemId, DateTimeOffset? start, DateTimeOffset? recurrence)
    {
        if (Store?.GetItem(itemId) is not { } item)
        {
            return;
        }
        OpenResult(new SearchResult(item.Id, item.CollectionId, item.Kind, item.Summary ?? "", null,
            start ?? item.StartUtc ?? item.DueUtc, null, item.AllDay, recurrence, item.IsRecurring, false));
    }

    /// <summary>The missed-reminder summary's click (reminders-notifications R6): Agenda, scrolled to today.</summary>
    internal void OpenAgendaToday()
    {
        if (_view == ViewKind.Agenda)
        {
            ScrollToToday();
        }
        else
        {
            SwitchView(ViewKind.Agenda);
        }
    }

    /// <summary>Scrolls to the item's day and opens it in its editor; also used by the conflicts list.</summary>
    private void OpenResult(SearchResult result)
    {
        var stayInTasks = result.Kind == ItemKind.Task && _view == ViewKind.Tasks;
        if (result.StartUtc is { } start && !stayInTasks)
        {
            var local = start.ToLocalTime().DateTime;
            var day = DateOnly.FromDateTime(local);
            if (_view == ViewKind.Tasks)
            {
                _pendingDay = day;
                SwitchView(ViewKind.Week);
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
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        if (result.IsSeries)
        {
            title.Inlines.Add(new Run { Text = "  ", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 12 });
        }
        var details = new SpacedStack { Orientation = Orientation.Horizontal, Spacing = 6 };
        details.Children.Add(Caption(When(result)));
        details.Children.Add(ShapeMarker.Create(collection.Shape, Theme.Calendar(collection.Colour).Swatch));
        details.Children.Add(Caption(collection.Name));
        var text = new SpacedStack { Spacing = 2 };
        text.Children.Add(title);
        text.Children.Add(details);
        if (result.Snippet is { } snippet)
        {
            var line = Marked(snippet.ReplaceLineEndings(" "), Theme.Style("CaptionTextBlockStyle"));
            line.Foreground = Theme.Brush("TextSecondaryBrush");
            text.Children.Add(line);
        }
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var icon = ViewParts.Icon(result.Kind == ItemKind.Task ? "" : "", 16);
        icon.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);
        AutomationProperties.SetName(row,
            $"{(result.Kind == ItemKind.Task ? "Task" : "Event")} {Plain(result.Title)}, {When(result)}, {collection.Name}");
        return row;
    }

    /// <summary>A line in the list that is not a result (no results, the MS365 window); choosing it does nothing.</summary>
    private static ListBoxItem NoteRow(string text)
    {
        var note = Caption(text);
        note.Margin = new Thickness(0, 4, 0, 4);
        return new ListBoxItem { Content = note, Focusable = false, IsHitTestVisible = false };
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
