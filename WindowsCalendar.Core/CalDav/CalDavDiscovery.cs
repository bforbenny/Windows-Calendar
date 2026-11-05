using System.Xml.Linq;
using WindowsCalendar.Core.Store;
using static WindowsCalendar.Core.CalDav.DavTransport;

namespace WindowsCalendar.Core.CalDav;

/// <summary>
/// Finds the calendars and task lists of a CalDAV account (account-management R4, sync-radicale R1):
/// current-user-principal, then calendar-home-set, then PROPFIND Depth 1 on the home.
/// Sends PROPFIND only, so it never changes the server.
/// </summary>
/// <param name="http">A client that does not follow redirects itself (see <see cref="DavTransport"/>).</param>
public sealed class CalDavDiscovery(HttpClient http)
{
    /// <summary>
    /// Tests the connection and lists the collections. A server root tries /.well-known/caldav, then /;
    /// any other path is treated as the principal (or a URL that leads to it).
    /// A collection that holds both events and tasks becomes two entries with the same URL (account-management R1).
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredCollection>> DiscoverAsync(Uri url, string username, string password,
        CancellationToken cancellationToken = default)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            throw new CalDavException(CalDavError.NotHttps);
        }

        var dav = new DavTransport(http, username, password);

        Uri[] starts = url.AbsolutePath is "" or "/"
            ? [new Uri(url, "/.well-known/caldav"), new Uri(url, "/")]
            : [url];

        Uri? principal = null;
        CalDavException? lastError = null;
        foreach (var start in starts)
        {
            try
            {
                var (doc, at) = await dav.SendAsync(Propfind, start, 0,
                    Body(Dav + "propfind", new XElement(Dav + "prop", new XElement(Dav + "current-user-principal"))),
                    cancellationToken);
                principal = Hrefs(doc, at, Dav + "current-user-principal").FirstOrDefault();
                if (principal is not null)
                {
                    break;
                }
            }
            catch (CalDavException ex) when (ex.Error == CalDavError.NotCalDav)
            {
                lastError = ex;
            }
        }
        if (principal is null)
        {
            throw lastError ?? new CalDavException(CalDavError.NotCalDav);
        }

        var (principalDoc, principalAt) = await dav.SendAsync(Propfind, principal, 0,
            Body(Dav + "propfind", new XElement(Dav + "prop", new XElement(CalDavNs + "calendar-home-set"))),
            cancellationToken);
        var home = Hrefs(principalDoc, principalAt, CalDavNs + "calendar-home-set").FirstOrDefault()
            ?? throw new CalDavException(CalDavError.NotCalDav);

        var (homeDoc, homeAt) = await dav.SendAsync(Propfind, home, 1,
            Body(Dav + "propfind", new XElement(Dav + "prop",
                new XElement(Dav + "resourcetype"),
                new XElement(Dav + "displayname"),
                new XElement(Dav + "current-user-privilege-set"),
                new XElement(CalDavNs + "supported-calendar-component-set"))),
            cancellationToken);

        var result = new List<DiscoveredCollection>();
        foreach (var response in homeDoc.Root!.Elements(Dav + "response"))
        {
            var props = OkProps(response).ToList();
            if (!props.Any(p => p.Element(Dav + "resourcetype")?.Element(CalDavNs + "calendar") is not null))
            {
                continue;
            }

            var href = new Uri(homeAt, response.Element(Dav + "href")!.Value.Trim());
            var name = props.Select(p => p.Element(Dav + "displayname")?.Value.Trim()).FirstOrDefault(n => !string.IsNullOrEmpty(n))
                ?? Uri.UnescapeDataString(href.AbsolutePath.TrimEnd('/').Split('/')[^1]);

            // No privilege set reported: assume writable. Reported without write: read-only.
            var privileges = props.Select(p => p.Element(Dav + "current-user-privilege-set")).FirstOrDefault(e => e is not null);
            var readOnly = privileges is not null && !privileges.Descendants(Dav + "privilege")
                .Any(p => p.Element(Dav + "write") is not null || p.Element(Dav + "all") is not null);

            // No component set reported means any component type (RFC 4791 5.2.3).
            var componentSet = props.Select(p => p.Element(CalDavNs + "supported-calendar-component-set")).FirstOrDefault(e => e is not null);
            var components = componentSet?.Elements(CalDavNs + "comp").Select(c => (string?)c.Attribute("name")).ToHashSet()
                ?? ["VEVENT", "VTODO"];

            if (components.Contains("VEVENT"))
            {
                result.Add(new DiscoveredCollection(href.AbsoluteUri, name, CollectionKind.Calendar, readOnly));
            }
            if (components.Contains("VTODO"))
            {
                result.Add(new DiscoveredCollection(href.AbsoluteUri, name, CollectionKind.TaskList, readOnly));
            }
        }

        return result.Count > 0 ? result : throw new CalDavException(CalDavError.NoCalendars);
    }

    /// <summary>Hrefs inside the named property of any 200 propstat, resolved against the answering URI.</summary>
    private static IEnumerable<Uri> Hrefs(XDocument doc, Uri baseUri, XName property) =>
        doc.Root!.Elements(Dav + "response")
            .SelectMany(OkProps)
            .Select(p => p.Element(property))
            .OfType<XElement>()
            .SelectMany(e => e.Elements(Dav + "href"))
            .Select(h => new Uri(baseUri, h.Value.Trim()));
}
