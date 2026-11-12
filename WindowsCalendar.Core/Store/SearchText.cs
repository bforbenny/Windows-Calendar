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
    /// The names and email addresses of a stored payload's attendees and organiser, one per line (search-attendees R1):
    /// Graph <c>attendees</c> and <c>organizer</c>, or CalDAV <c>ATTENDEE</c> and <c>ORGANIZER</c> (CN and address). An
    /// entry with <paramref name="ownEmail"/>, the account's own address, is skipped whole (R2). Null when there are
    /// none or it can't be read.
    /// </summary>
    public static string? People(string payload, string? ownEmail)
    {
        try
        {
            var own = ownEmail?.Trim();
            var found = new List<string>();
            void Add(string? name, string? address)
            {
                address = address?.Trim();
                if (!string.IsNullOrEmpty(own) && string.Equals(address, own, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                found.Add(name?.Trim() ?? "");
                found.Add(address ?? "");
            }
            if (payload.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(payload);
                var ev = doc.RootElement;
                var recipients = new List<JsonElement>();
                if (ev.TryGetProperty("organizer", out var organiser))
                {
                    recipients.Add(organiser);
                }
                if (ev.TryGetProperty("attendees", out var attendees) && attendees.ValueKind == JsonValueKind.Array)
                {
                    recipients.AddRange(attendees.EnumerateArray());
                }
                foreach (var recipient in recipients)
                {
                    if (recipient.ValueKind == JsonValueKind.Object && recipient.TryGetProperty("emailAddress", out var e)
                        && e.ValueKind == JsonValueKind.Object)
                    {
                        Add(Graph.GraphClient.Text(e, "name"), Graph.GraphClient.Text(e, "address"));
                    }
                }
            }
            else if (Ical.Net.Calendar.Load(payload) is { Events.Count: > 0 } calendar)
            {
                var master = CalDav.CalDavEvents.Master(calendar);
                if (master.Organizer is { } organiser)
                {
                    Add(organiser.CommonName, MailAddress(organiser.Value));
                }
                foreach (var attendee in master.Attendees)
                {
                    Add(attendee.CommonName, MailAddress(attendee.Value));
                }
            }
            var text = string.Join("\n", found.Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
            return text.Length == 0 ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? MailAddress(Uri? value)
    {
        var address = value?.OriginalString.Trim();
        return address is not null && address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? address["mailto:".Length..] : address;
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

    /// <summary>
    /// The FTS5 phrase for one name or address: its words in order, exact (no prefix), as one quoted phrase
    /// (search-filters R2). Null when there are no words.
    /// </summary>
    public static string? Phrase(string text)
    {
        var words = Word().Matches(text).Select(m => m.Value).ToList();
        return words.Count == 0 ? null : $"\"{string.Join(" ", words)}\"";
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
