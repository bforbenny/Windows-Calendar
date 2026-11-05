using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Sync;

/// <summary>
/// Subscribed calendars (sync-radicale R1a): downloads the feed a Radicale subscription points to, at most hourly
/// unless forced, and caches one read-only item per UID. No credentials are sent; HTTPS only; the URL is never logged.
/// </summary>
/// <param name="http">A client that does not follow redirects itself; this class follows HTTPS redirects only.</param>
public sealed class FeedSync(LocalStore store, HttpClient http, TimeProvider? time = null)
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    private const string CheckedKey = "feed-checked";
    private const string EtagKey = "feed-etag";
    private const string ModifiedKey = "feed-modified";
    private const int MaxRedirects = 5;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The URL to fetch: https as is, webcal as https; null for anything else (not secure).</summary>
    public static Uri? FetchUrl(string? source)
    {
        if (source is null || !Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return null;
        }
        if (uri.Scheme == "webcal" || uri.Scheme == "webcals")
        {
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri;
        }
        return uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    /// <summary>
    /// Refreshes one subscribed collection if it is due (or <paramref name="force"/>). Returns the number of items
    /// added, changed or removed. Throws <see cref="CalDavException"/> when the feed cannot be fetched or read; the
    /// check time is recorded first, so a failing feed is tried again an hour later, not in a loop.
    /// </summary>
    public async Task<int> RefreshAsync(Collection collection, bool force, CancellationToken cancellationToken = default)
    {
        if (FetchUrl(collection.SourceUrl) is not { } url)
        {
            return 0;
        }
        var now = _time.GetUtcNow();
        if (!force && store.GetSyncState(collection.Id, CheckedKey) is { } last
            && now - DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(last)) < RefreshInterval)
        {
            return 0;
        }
        store.SetSyncState(collection.Id, CheckedKey, now.ToUnixTimeMilliseconds().ToString());

        using var response = await GetAsync(url, store.GetSyncState(collection.Id, EtagKey),
            store.GetSyncState(collection.Id, ModifiedKey), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return 0;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new CalDavException(CalDavError.NotCalDav, response.StatusCode);
        }
        var data = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!data.TrimStart().StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            throw new CalDavException(CalDavError.NotCalDav, response.StatusCode);
        }

        var ids = new[] { collection.Id };
        var local = store.GetRemoteEtags(ids);
        var zone = _time.LocalTimeZone;
        var seen = new HashSet<string>();
        var changes = 0;
        foreach (var (uid, payload) in Split(data))
        {
            var remoteId = collection.RemoteRef + "#" + Uri.EscapeDataString(uid);
            var etag = Hash(payload);
            seen.Add(remoteId);
            if (local.TryGetValue(remoteId, out var known) && known == etag)
            {
                continue;
            }
            if (CalDavItemMapper.Map(remoteId, etag, payload, zone) is { Kind: ItemKind.Event } item)
            {
                store.ApplyServerItem(ids, item with { Id = Guid.NewGuid().ToString("N"), CollectionId = collection.Id });
                changes++;
            }
        }
        foreach (var gone in local.Keys.Where(k => !seen.Contains(k)))
        {
            store.RemoveServerItem(ids, gone);
            changes++;
        }

        SetOrClear(collection.Id, EtagKey, response.Headers.ETag?.ToString());
        SetOrClear(collection.Id, ModifiedKey, response.Content.Headers.LastModified?.ToString("R"));
        return changes;
    }

    /// <summary>
    /// Splits a feed into one calendar object per UID: the feed's timezones plus every VEVENT with that UID (master and
    /// overrides). Components are copied as text, so nothing is lost or reformatted.
    /// </summary>
    internal static IEnumerable<(string Uid, string Payload)> Split(string data)
    {
        var lines = data.Replace("\r\n", "\n").Split('\n');
        var timezones = new StringBuilder();
        var events = new Dictionary<string, StringBuilder>();
        var order = new List<string>();
        StringBuilder? block = null;
        string? end = null;
        foreach (var line in lines)
        {
            if (block is null)
            {
                if (line.Equals("BEGIN:VTIMEZONE", StringComparison.OrdinalIgnoreCase)
                    || line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    block = new StringBuilder();
                    end = "END:" + line[6..];
                }
                else
                {
                    continue;
                }
            }
            block.Append(line).Append("\r\n");
            if (!line.Equals(end, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var text = block.ToString();
            block = null;
            if (end.Equals("END:VTIMEZONE", StringComparison.OrdinalIgnoreCase))
            {
                timezones.Append(text);
            }
            else if (Uid(text) is { } uid)
            {
                if (!events.TryGetValue(uid, out var sb))
                {
                    events[uid] = sb = new StringBuilder();
                    order.Add(uid);
                }
                sb.Append(text);
            }
        }
        foreach (var uid in order)
        {
            yield return (uid, "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//WindowsCalendar//Feed//EN\r\n"
                + timezones + events[uid] + "END:VCALENDAR\r\n");
        }
    }

    /// <summary>The UID of a VEVENT block (the top-level one, unfolded).</summary>
    private static string? Uid(string block)
    {
        var unfolded = block.Replace("\r\n ", "").Replace("\r\n\t", "");
        foreach (var line in unfolded.Split("\r\n"))
        {
            if (line.StartsWith("UID", StringComparison.OrdinalIgnoreCase) && line.IndexOf(':') is var colon and > 2
                && (line[3] == ':' || line[3] == ';'))
            {
                return line[(colon + 1)..].Trim();
            }
        }
        return null;
    }

    private static string Hash(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..32];

    private void SetOrClear(string collectionId, string key, string? value)
    {
        if (value is not null)
        {
            store.SetSyncState(collectionId, key, value);
        }
    }

    /// <summary>GET without credentials, following redirects only to HTTPS.</summary>
    private async Task<HttpResponseMessage> GetAsync(Uri url, string? etag, string? modified, CancellationToken cancellationToken)
    {
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (etag is not null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }
            if (modified is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Modified-Since", modified);
            }

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
            {
                throw new CalDavException(CalDavError.CertificateNotTrusted, inner: ex);
            }
            catch (HttpRequestException ex)
            {
                throw new CalDavException(CalDavError.Unreachable, inner: ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CalDavException(CalDavError.Unreachable, inner: ex);
            }

            if ((int)response.StatusCode is >= 300 and < 400 && response.StatusCode != HttpStatusCode.NotModified
                && response.Headers.Location is { } location && hop < MaxRedirects)
            {
                response.Dispose();
                var next = new Uri(url, location);
                if (next.Scheme != Uri.UriSchemeHttps)
                {
                    throw new CalDavException(CalDavError.NotHttps);
                }
                url = next;
                continue;
            }
            return response;
        }
    }
}
