using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Xml.Linq;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.CalDav;

/// <summary>Why a connection test failed (account-management R4a). Each has its own message in the UI.</summary>
public enum DiscoveryError { NotHttps, Unreachable, CertificateNotTrusted, WrongCredentials, NotCalDav, NoCalendars }

/// <summary>
/// A failed connection test. The message never contains the URL's credentials, the username or the password.
/// </summary>
public sealed class CalDavDiscoveryException(DiscoveryError error, HttpStatusCode? status = null, Exception? inner = null)
    : Exception($"CalDAV discovery failed: {error}{(status is null ? "" : $" (HTTP {(int)status})")}", inner)
{
    public DiscoveryError Error { get; } = error;
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Finds the calendars and task lists of a CalDAV account (account-management R4, sync-radicale R1):
/// current-user-principal, then calendar-home-set, then PROPFIND Depth 1 on the home.
/// Sends PROPFIND only, so it never changes the server.
/// </summary>
/// <param name="http">A client that does not follow redirects itself (redirects are followed here, keeping
/// the Authorization header on the same host).</param>
public sealed class CalDavDiscovery(HttpClient http)
{
    private const int MaxRedirects = 5;

    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";

    private static readonly HttpMethod Propfind = new("PROPFIND");

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
            throw new CalDavDiscoveryException(DiscoveryError.NotHttps);
        }

        var auth = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

        Uri[] starts = url.AbsolutePath is "" or "/"
            ? [new Uri(url, "/.well-known/caldav"), new Uri(url, "/")]
            : [url];

        Uri? principal = null;
        CalDavDiscoveryException? lastError = null;
        foreach (var start in starts)
        {
            try
            {
                var doc = await PropfindAsync(start, 0, auth,
                    Prop(new XElement(Dav + "current-user-principal")), cancellationToken);
                principal = Hrefs(doc, start, Dav + "current-user-principal").FirstOrDefault();
                if (principal is not null)
                {
                    break;
                }
            }
            catch (CalDavDiscoveryException ex) when (ex.Error == DiscoveryError.NotCalDav)
            {
                lastError = ex;
            }
        }
        if (principal is null)
        {
            throw lastError ?? new CalDavDiscoveryException(DiscoveryError.NotCalDav);
        }

        var principalDoc = await PropfindAsync(principal, 0, auth,
            Prop(new XElement(CalDav + "calendar-home-set")), cancellationToken);
        var home = Hrefs(principalDoc, principal, CalDav + "calendar-home-set").FirstOrDefault()
            ?? throw new CalDavDiscoveryException(DiscoveryError.NotCalDav);

        var homeDoc = await PropfindAsync(home, 1, auth,
            Prop(
                new XElement(Dav + "resourcetype"),
                new XElement(Dav + "displayname"),
                new XElement(Dav + "current-user-privilege-set"),
                new XElement(CalDav + "supported-calendar-component-set")),
            cancellationToken);

        var result = new List<DiscoveredCollection>();
        foreach (var response in homeDoc.Root!.Elements(Dav + "response"))
        {
            var props = OkProps(response).ToList();
            if (!props.Any(p => p.Element(Dav + "resourcetype")?.Element(CalDav + "calendar") is not null))
            {
                continue;
            }

            var href = new Uri(home, response.Element(Dav + "href")!.Value.Trim());
            var name = props.Select(p => p.Element(Dav + "displayname")?.Value.Trim()).FirstOrDefault(n => !string.IsNullOrEmpty(n))
                ?? Uri.UnescapeDataString(href.AbsolutePath.TrimEnd('/').Split('/')[^1]);

            // No privilege set reported: assume writable. Reported without write: read-only.
            var privileges = props.Select(p => p.Element(Dav + "current-user-privilege-set")).FirstOrDefault(e => e is not null);
            var readOnly = privileges is not null && !privileges.Descendants(Dav + "privilege")
                .Any(p => p.Element(Dav + "write") is not null || p.Element(Dav + "all") is not null);

            // No component set reported means any component type (RFC 4791 5.2.3).
            var componentSet = props.Select(p => p.Element(CalDav + "supported-calendar-component-set")).FirstOrDefault(e => e is not null);
            var components = componentSet?.Elements(CalDav + "comp").Select(c => (string?)c.Attribute("name")).ToHashSet()
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

        return result.Count > 0 ? result : throw new CalDavDiscoveryException(DiscoveryError.NoCalendars);
    }

    private static XElement Prop(params XElement[] props) =>
        new(Dav + "propfind", new XAttribute(XNamespace.Xmlns + "d", Dav), new XAttribute(XNamespace.Xmlns + "c", CalDav),
            new XElement(Dav + "prop", props));

    /// <summary>Sends PROPFIND, following same-host HTTPS redirects, and returns the 207 multistatus body.</summary>
    private async Task<XDocument> PropfindAsync(Uri uri, int depth, AuthenticationHeaderValue auth, XElement body,
        CancellationToken cancellationToken)
    {
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(Propfind, uri)
            {
                Content = new StringContent(body.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/xml"),
            };
            request.Headers.Authorization = auth;
            request.Headers.Add("Depth", depth.ToString());

            using var response = await SendAsync(request, cancellationToken);
            var status = response.StatusCode;

            if (status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                && response.Headers.Location is { } location && hop < MaxRedirects)
            {
                var next = new Uri(uri, location);
                // Never send the credentials to another host or over plain HTTP.
                if (next.Scheme != Uri.UriSchemeHttps || next.Host != uri.Host || next.Port != uri.Port)
                {
                    throw new CalDavDiscoveryException(DiscoveryError.NotCalDav, status);
                }
                uri = next;
                continue;
            }

            if (status == HttpStatusCode.Unauthorized)
            {
                throw new CalDavDiscoveryException(DiscoveryError.WrongCredentials, status);
            }
            if (status != HttpStatusCode.MultiStatus)
            {
                throw new CalDavDiscoveryException(DiscoveryError.NotCalDav, status);
            }

            try
            {
                return XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            }
            catch (System.Xml.XmlException ex)
            {
                throw new CalDavDiscoveryException(DiscoveryError.NotCalDav, status, ex);
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            throw new CalDavDiscoveryException(DiscoveryError.CertificateNotTrusted, inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new CalDavDiscoveryException(DiscoveryError.Unreachable, inner: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient timeout.
            throw new CalDavDiscoveryException(DiscoveryError.Unreachable, inner: ex);
        }
    }

    /// <summary>The prop elements of a response's propstats that have status 200.</summary>
    private static IEnumerable<XElement> OkProps(XElement response) =>
        response.Elements(Dav + "propstat")
            .Where(ps => ps.Element(Dav + "status")?.Value.Contains(" 200 ") == true)
            .Select(ps => ps.Element(Dav + "prop"))
            .OfType<XElement>();

    /// <summary>Hrefs inside the named property of any 200 propstat, resolved against the request URI.</summary>
    private static IEnumerable<Uri> Hrefs(XDocument doc, Uri baseUri, XName property) =>
        doc.Root!.Elements(Dav + "response")
            .SelectMany(OkProps)
            .Select(p => p.Element(property))
            .OfType<XElement>()
            .SelectMany(e => e.Elements(Dav + "href"))
            .Select(h => new Uri(baseUri, h.Value.Trim()));
}
