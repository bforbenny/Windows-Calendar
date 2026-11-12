using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.QuickAdd;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar;

/// <summary>
/// Typed date chips (search-date-chips R3-R6): a date phrase typed in the search box ("next week", "monday") becomes a
/// date chip, first in the chip row. It is found on each search run (inside the 150 ms debounce), never changes the text
/// in the box, and takes its words out of the FTS words.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The typed date chip for the current text: its label, range, the consumed words and the words left over.</summary>
    private sealed record TypedDate(string Label, DateOnly From, DateOnly ToExclusive, string Consumed, string Remaining);

    private TypedDate? _typedDate;

    /// <summary>The exact box text whose typed chip the user removed (R4); any text change makes the chip eligible again.</summary>
    private string? _dismissedDateText;

    /// <summary>R3, R4: finds the date phrase in <paramref name="text"/> unless dismissed for this exact text.</summary>
    private void UpdateTypedDate(string text)
    {
        _typedDate = null;
        if (_dismissedDateText is not null && text != _dismissedDateText)
        {
            _dismissedDateText = null;
        }
        if (_dismissedDateText is not null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        // Same day-first rule as the quick-add options (MainWindow.QuickAdd.cs).
        var pattern = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
        var dayFirst = pattern.IndexOf('d') < pattern.IndexOf('M');
        if (QuickAddParser.FindDatePhrase(text, _today, dayFirst) is not { } phrase)
        {
            return;
        }
        var range = SearchDates.Range(phrase, _today, WeekStart);
        var label = SearchDates.Label(phrase, range, CultureInfo.CurrentCulture);
        if (_graphAccounts.Count > 0 && range.From < GraphSync.WindowStart(_today))
        {
            label += $" · searched from {GraphSync.WindowStart(_today).ToString("d MMM yyyy", CultureInfo.CurrentCulture)}";
        }
        var (offset, length) = phrase.Span.GetOffsetAndLength(text.Length);
        var remaining = Regex.Replace(text.Remove(offset, length), @"\s+", " ").Trim();
        _typedDate = new TypedDate(label, range.From, range.ToExclusive, text.Substring(offset, length), remaining);
    }

    /// <summary>
    /// The hand-set filters plus the typed date range (R6): both date ranges apply, so the dates are intersected; an
    /// empty intersection gives no results, and the empty fallback takes over.
    /// </summary>
    private SearchFilter? EffectiveSearchFilter(SearchFilter? hand)
    {
        if (_typedDate is not { } typed)
        {
            return hand;
        }
        var from = new DateTimeOffset(typed.From.ToDateTime(TimeOnly.MinValue));
        var to = new DateTimeOffset(typed.ToExclusive.ToDateTime(TimeOnly.MinValue));
        var filter = hand ?? new SearchFilter();
        return filter with
        {
            FromUtc = filter.FromUtc is { } f && f > from ? f : from,
            ToUtc = filter.ToUtc is { } t && t < to ? t : to,
        };
    }

    /// <summary>
    /// R5: the consumed words also searched in titles, with the hand-set filters but not the typed range; matches not
    /// already listed go under a note after the filtered results. Returns the result rows added.
    /// </summary>
    private int AddConsumedTitleMatches(LocalStore store, SearchFilter? hand)
    {
        if (_typedDate is not { } typed)
        {
            return 0;
        }
        var titles = store.Search(typed.Remaining, (hand ?? new SearchFilter()) with { TitleWords = typed.Consumed }, 10);
        var listed = SearchResults.Items.OfType<ListBoxItem>()
            .Select(i => (i.Tag as SearchResult)?.ItemId).Where(id => id is not null).ToHashSet();
        var fresh = titles.Where(r => !listed.Contains(r.ItemId)).ToList();
        if (fresh.Count == 0)
        {
            return 0;
        }
        var note = NoteRow($"Titles with \"{typed.Consumed}\"");
        SearchResults.Items.Add(note);
        var shown = AddResults(fresh);
        if (shown == 0)
        {
            SearchResults.Items.Remove(note);
        }
        return shown;
    }

    /// <summary>The typed chip (R3, R6): same look and removal as the other chips; removing it dismisses it for this text (R4).</summary>
    private FrameworkElement? TypedDateChip()
    {
        if (_typedDate is not { } typed)
        {
            return null;
        }
        return MakeChip(typed.Label, $"Date filter {typed.Label}, from typed text", () =>
        {
            _dismissedDateText = SearchBox.Text;
            ApplySearchFilters();
            SearchBox.Focus();
        });
    }
}
