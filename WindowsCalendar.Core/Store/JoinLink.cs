using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ical.Net.CalendarComponents;

namespace WindowsCalendar.Core.Store;

/// <summary>An online-meeting link the app may open, with its provider's name (online-meeting-join R2).</summary>
public sealed partial record JoinLink(string Url, string Provider)
{
    /// <summary>The link when it is HTTPS on a recognised provider's host (R2); null for anything else.</summary>
    public static JoinLink? Recognise(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }
        var host = uri.Host.ToLowerInvariant();
        var provider = host switch
        {
            "teams.microsoft.com" or "teams.live.com" => "Teams",
            "zoom.us" => "Zoom",
            _ when host.EndsWith(".zoom.us", StringComparison.Ordinal) => "Zoom",
            "meet.google.com" => "Google Meet",
            _ when host.EndsWith(".webex.com", StringComparison.Ordinal) => "Webex",
            _ => null,
        };
        return provider is null ? null : new JoinLink(uri.AbsoluteUri, provider);
    }

    /// <summary>The first recognised link in free text (a location or description; HTML is fine).</summary>
    public static JoinLink? FirstIn(string? text) =>
        text is null ? null
            : UrlPattern().Matches(text)
                .Select(m => Recognise(WebUtility.HtmlDecode(m.Value).TrimEnd('.', ',', ';', ':', ')', ']', '}', '!', '?')))
                .FirstOrDefault(l => l is not null);

    /// <summary>
    /// R1 for a Graph event: <c>onlineMeeting.joinUrl</c> when <c>isOnlineMeeting</c>, then <c>onlineMeetingUrl</c>,
    /// then the location, then the body. None for a meeting you declined (R6a).
    /// </summary>
    public static JoinLink? FromGraph(JsonElement ev)
    {
        if (ev.TryGetProperty("responseStatus", out var response) && Text(response, "response") == "declined")
        {
            return null;
        }
        var online = ev.TryGetProperty("isOnlineMeeting", out var flag) && flag.ValueKind == JsonValueKind.True
            && ev.TryGetProperty("onlineMeeting", out var meeting) && meeting.ValueKind == JsonValueKind.Object
            ? Recognise(Text(meeting, "joinUrl")) : null;
        return online
            ?? Recognise(Text(ev, "onlineMeetingUrl"))
            ?? FirstIn(Graph.GraphEventMapper.Location(ev))
            ?? (ev.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object ? FirstIn(Text(body, "content")) : null);
    }

    /// <summary>R1 for a CalDAV event: URL, X-GOOGLE-CONFERENCE, X-MICROSOFT-SKYPETEAMSMEETINGURL, then location and description.</summary>
    public static JoinLink? FromCalDav(CalendarEvent ev) =>
        Recognise(ev.Url?.OriginalString)
        ?? PropertyNames.Select(n => Recognise(ev.Properties.Get<string>(n))).FirstOrDefault(l => l is not null)
        ?? FirstIn(ev.Location)
        ?? FirstIn(ev.Description);

    /// <summary>The CalDAV properties besides URL that may hold a join link (R1); dropped when duplicating (R7).</summary>
    public static readonly string[] PropertyNames = ["X-GOOGLE-CONFERENCE", "X-MICROSOFT-SKYPETEAMSMEETINGURL"];

    /// <summary>The link from a stored payload: Graph JSON or iCalendar; null when it can't be read.</summary>
    public static JoinLink? FromPayload(string payload)
    {
        try
        {
            if (payload.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(payload);
                return FromGraph(doc.RootElement);
            }
            return Ical.Net.Calendar.Load(payload) is { Events.Count: > 0 } calendar ? FromCalDav(CalDav.CalDavEvents.Master(calendar)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Graph may send an object as null (e.g. responseStatus in a colleague's calendar).
    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"https://[^\s<>""'\\]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}
