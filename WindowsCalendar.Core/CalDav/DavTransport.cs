using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Xml.Linq;

namespace WindowsCalendar.Core.CalDav;

/// <summary>
/// Sends WebDAV requests (PROPFIND, REPORT, PUT, DELETE) with Basic auth and returns the 207 multistatus body
/// (sync-radicale R5, R10). Follows redirects itself, only on the same HTTPS host, so the Authorization header
/// never leaves the server. The HttpClient must not follow redirects.
/// </summary>
internal sealed class DavTransport
{
    public static readonly XNamespace Dav = "DAV:";
    public static readonly XNamespace CalDavNs = "urn:ietf:params:xml:ns:caldav";
    public static readonly XNamespace CalendarServer = "http://calendarserver.org/ns/";
    public static readonly XNamespace AppleIcal = "http://apple.com/ns/ical/";
    public static readonly HttpMethod Propfind = new("PROPFIND");
    public static readonly HttpMethod Report = new("REPORT");

    private const int MaxRedirects = 5;

    private readonly HttpClient _http;
    private readonly AuthenticationHeaderValue _auth;

    public DavTransport(HttpClient http, string username, string password)
    {
        _http = http;
        _auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
    }

    /// <summary>A request body with the DAV: and CalDAV namespaces declared.</summary>
    public static XElement Body(XName root, params object[] content) =>
        new(root, new XAttribute(XNamespace.Xmlns + "d", Dav), new XAttribute(XNamespace.Xmlns + "c", CalDavNs),
            new XAttribute(XNamespace.Xmlns + "cs", CalendarServer), new XAttribute(XNamespace.Xmlns + "ic", AppleIcal), content);

    /// <summary>Returns the multistatus document and the URI that answered (after redirects).</summary>
    public async Task<(XDocument Doc, Uri Uri)> SendAsync(HttpMethod method, Uri uri, int depth, XElement body,
        CancellationToken cancellationToken)
    {
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(method, uri)
            {
                Content = new StringContent(body.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/xml"),
            };
            request.Headers.Authorization = _auth;
            request.Headers.Add("Depth", depth.ToString());

            using var response = await SendRawAsync(request, cancellationToken);
            var status = response.StatusCode;

            if (status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                && response.Headers.Location is { } location && hop < MaxRedirects)
            {
                var next = new Uri(uri, location);
                // Never send the credentials to another host or over plain HTTP.
                if (next.Scheme != Uri.UriSchemeHttps || next.Host != uri.Host || next.Port != uri.Port)
                {
                    throw new CalDavException(CalDavError.NotCalDav, status);
                }
                uri = next;
                continue;
            }

            if (status == HttpStatusCode.Unauthorized)
            {
                throw new CalDavException(CalDavError.WrongCredentials, status);
            }
            if (status != HttpStatusCode.MultiStatus)
            {
                throw new CalDavException(CalDavError.NotCalDav, status);
            }

            try
            {
                return (XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)), uri);
            }
            catch (System.Xml.XmlException ex)
            {
                throw new CalDavException(CalDavError.NotCalDav, status, ex);
            }
        }
    }

    /// <summary>
    /// Sends a PUT or DELETE (sync-radicale R6) and returns the status and the ETag header, if any. Redirects are not
    /// followed: a resource URL comes from the server itself. 401 throws, like every other request.
    /// </summary>
    public async Task<(HttpStatusCode Status, string? Etag)> WriteAsync(HttpMethod method, Uri uri, string? calendarData,
        string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (calendarData is not null)
        {
            request.Content = new StringContent(calendarData, Encoding.UTF8, "text/calendar");
        }
        request.Headers.Authorization = _auth;
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }
        if (ifNoneMatch)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        }

        using var response = await SendRawAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new CalDavException(CalDavError.WrongCredentials, response.StatusCode);
        }
        return (response.StatusCode, response.Headers.ETag?.ToString());
    }

    /// <summary>The prop elements of a response's propstats that have status 200.</summary>
    public static IEnumerable<XElement> OkProps(XElement response) =>
        response.Elements(Dav + "propstat")
            .Where(ps => ps.Element(Dav + "status")?.Value.Contains(" 200 ") == true)
            .Select(ps => ps.Element(Dav + "prop"))
            .OfType<XElement>();

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, cancellationToken);
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
            // HttpClient timeout.
            throw new CalDavException(CalDavError.Unreachable, inner: ex);
        }
    }
}
