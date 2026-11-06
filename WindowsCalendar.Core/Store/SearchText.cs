using System.Text.Json;
using System.Text.RegularExpressions;
using Ical.Net.CalendarComponents;

namespace WindowsCalendar.Core.Store;

/// <summary>What the search index holds for an item besides its title and location (search R1).</summary>
internal static partial class SearchText
{
    /// <summary>
    /// The description or notes from a stored payload, as plain text: the Graph body, or the DESCRIPTION of every
    /// event or to-do in the iCalendar (with an exception's own title and location). Null when it can't be read.
    /// </summary>
    public static string? Body(string payload)
    {
        try
        {
            if (payload.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(payload);
                var ev = doc.RootElement;
                return ev.TryGetProperty("body", out var body) && Graph.GraphClient.Text(body, "contentType") == "html"
                    && Graph.GraphClient.Text(body, "content") is { } html
                    ? Sync.EventTransfer.PlainText(html)
                    : Graph.GraphEventMapper.Description(ev);
            }
            if (Ical.Net.Calendar.Load(payload) is not { } calendar)
            {
                return null;
            }
            var parts = new List<string?>();
            foreach (var c in calendar.Children.OfType<RecurringComponent>())
            {
                if (c.RecurrenceIdentifier is not null)
                {
                    parts.Add(c.Summary);
                    parts.Add((c as CalendarEvent)?.Location);
                }
                parts.Add(c.Description);
            }
            var text = string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct());
            return text.Length == 0 ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The FTS5 query for typed text: every word must match, each as a prefix (R2). Null when there are no words.
    /// Words are letters and digits only, so nothing typed is read as query syntax.
    /// </summary>
    public static string? Query(string text)
    {
        var words = Word().Matches(text).Select(m => $"\"{m.Value}\"*").ToList();
        return words.Count == 0 ? null : string.Join(" ", words);
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
