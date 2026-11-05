using System.Net;
using System.Xml.Linq;
using static WindowsCalendar.Core.CalDav.DavTransport;

namespace WindowsCalendar.Core.CalDav;

/// <summary>Result of a sync-collection REPORT: hrefs are absolute URIs.</summary>
public sealed record SyncChanges(string? NewToken, IReadOnlyList<(string Href, string Etag)> Changed, IReadOnlyList<string> Removed);

/// <summary>CalDAV operations used by sync (sync-radicale R2, R3, R4, R6).</summary>
public sealed class CalDavClient
{
    private readonly DavTransport _dav;

    /// <param name="http">A client that does not follow redirects itself (see <see cref="DavTransport"/>).</param>
    public CalDavClient(HttpClient http, string username, string password) => _dav = new DavTransport(http, username, password);

    /// <summary>The collection's current sync-token, or null if the server reports none (R4 quick check).</summary>
    public async Task<string?> GetSyncTokenAsync(Uri collection, CancellationToken cancellationToken = default)
    {
        var (doc, _) = await _dav.SendAsync(Propfind, collection, 0,
            Body(Dav + "propfind", new XElement(Dav + "prop", new XElement(Dav + "sync-token"))), cancellationToken);
        return doc.Root!.Elements(Dav + "response").SelectMany(OkProps)
            .Select(p => p.Element(Dav + "sync-token")?.Value.Trim())
            .FirstOrDefault(t => !string.IsNullOrEmpty(t));
    }

    /// <summary>
    /// Changes since <paramref name="token"/>; a null token lists every resource (R2, R3).
    /// Returns null when the server rejects the token (403 / 409, DAV:valid-sync-token).
    /// </summary>
    public async Task<SyncChanges?> SyncCollectionAsync(Uri collection, string? token, CancellationToken cancellationToken = default)
    {
        XDocument doc;
        Uri at;
        try
        {
            (doc, at) = await _dav.SendAsync(Report, collection, 0,
                Body(Dav + "sync-collection",
                    new XElement(Dav + "sync-token", token ?? ""),
                    new XElement(Dav + "sync-level", "1"),
                    new XElement(Dav + "prop", new XElement(Dav + "getetag"))),
                cancellationToken);
        }
        catch (CalDavException ex) when (token is not null && ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Conflict)
        {
            return null;
        }

        var changed = new List<(string, string)>();
        var removed = new List<string>();
        foreach (var response in doc.Root!.Elements(Dav + "response"))
        {
            var href = new Uri(at, response.Element(Dav + "href")!.Value.Trim());
            if (href.AbsolutePath.TrimEnd('/') == at.AbsolutePath.TrimEnd('/'))
            {
                continue; // The collection itself.
            }
            if (response.Element(Dav + "status")?.Value.Contains(" 404 ") == true)
            {
                removed.Add(href.AbsoluteUri);
                continue;
            }
            var etag = OkProps(response).Select(p => p.Element(Dav + "getetag")?.Value.Trim()).FirstOrDefault(e => !string.IsNullOrEmpty(e));
            if (etag is not null)
            {
                changed.Add((href.AbsoluteUri, etag));
            }
        }
        return new SyncChanges(doc.Root.Element(Dav + "sync-token")?.Value.Trim(), changed, removed);
    }

    /// <summary>
    /// Writes a resource (R6): a create sends If-None-Match: *, an update If-Match with the base etag (when known).
    /// Returns the status and the new etag, if the server sent one.
    /// </summary>
    public Task<(HttpStatusCode Status, string? Etag)> PutAsync(Uri resource, string data, string? ifMatch, bool create,
        CancellationToken cancellationToken = default) =>
        _dav.WriteAsync(HttpMethod.Put, resource, data, create ? null : ifMatch, create, cancellationToken);

    /// <summary>Deletes a resource, with If-Match when the etag is known (R6).</summary>
    public async Task<HttpStatusCode> DeleteAsync(Uri resource, string? ifMatch, CancellationToken cancellationToken = default) =>
        (await _dav.WriteAsync(HttpMethod.Delete, resource, null, ifMatch, false, cancellationToken)).Status;

    /// <summary>Fetches the given resources (calendar-multiget). Hrefs are absolute URIs on the collection's server.</summary>
    public async Task<IReadOnlyList<(string Href, string Etag, string Data)>> MultigetAsync(Uri collection, IReadOnlyList<string> hrefs,
        CancellationToken cancellationToken = default)
    {
        var (doc, at) = await _dav.SendAsync(Report, collection, 1,
            Body(CalDavNs + "calendar-multiget",
                new XElement(Dav + "prop", new XElement(Dav + "getetag"), new XElement(CalDavNs + "calendar-data")),
                hrefs.Select(h => new XElement(Dav + "href", new Uri(h).AbsolutePath))),
            cancellationToken);

        var result = new List<(string, string, string)>();
        foreach (var response in doc.Root!.Elements(Dav + "response"))
        {
            var props = OkProps(response).ToList();
            var etag = props.Select(p => p.Element(Dav + "getetag")?.Value.Trim()).FirstOrDefault(e => !string.IsNullOrEmpty(e));
            var data = props.Select(p => p.Element(CalDavNs + "calendar-data")?.Value).FirstOrDefault(d => !string.IsNullOrEmpty(d));
            if (etag is not null && data is not null)
            {
                result.Add((new Uri(at, response.Element(Dav + "href")!.Value.Trim()).AbsoluteUri, etag, data));
            }
        }
        return result;
    }
}
