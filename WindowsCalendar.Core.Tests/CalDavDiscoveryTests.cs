using System.Net;
using System.Security.Authentication;
using System.Text;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

public sealed class CalDavDiscoveryTests
{
    private const string Server = "https://dav.example.test";

    /// <summary>Answers PROPFIND by path; records the requests.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = [];
        public List<HttpRequestMessage> Requests { get; } = [];
        public Exception? Throw { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Throw is not null)
            {
                throw Throw;
            }
            return Task.FromResult(Routes.TryGetValue(request.RequestUri!.AbsolutePath, out var route)
                ? route(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpResponseMessage MultiStatus(string responses) => new(HttpStatusCode.MultiStatus)
    {
        Content = new StringContent(
            $"""<?xml version="1.0"?><multistatus xmlns="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">{responses}</multistatus>""",
            Encoding.UTF8, "application/xml"),
    };

    private static string Ok(string href, string props) =>
        $"<response><href>{href}</href><propstat><prop>{props}</prop><status>HTTP/1.1 200 OK</status></propstat></response>";

    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    /// <summary>A Radicale-like server: well-known redirects to /, principal /ben/, two collections.</summary>
    private static FakeServer Radicale()
    {
        var server = new FakeServer();
        server.Routes["/.well-known/caldav"] = _ => Redirect("/");
        server.Routes["/"] = _ => MultiStatus(Ok("/", "<current-user-principal><href>/ben/</href></current-user-principal>"));
        server.Routes["/ben/"] = r => r.Headers.GetValues("Depth").Single() == "0"
            ? MultiStatus(Ok("/ben/", "<C:calendar-home-set><href>/ben/</href></C:calendar-home-set>"))
            : MultiStatus(
                Ok("/ben/", "<resourcetype><collection/><principal/></resourcetype>")
                + Ok("/ben/personal/",
                    """
                    <resourcetype><collection/><C:calendar/></resourcetype>
                    <displayname>Personal</displayname>
                    <current-user-privilege-set><privilege><read/></privilege><privilege><write/></privilege></current-user-privilege-set>
                    <C:supported-calendar-component-set><C:comp name="VEVENT"/><C:comp name="VTODO"/></C:supported-calendar-component-set>
                    """)
                + Ok("/ben/holidays/",
                    """
                    <resourcetype><collection/><C:calendar/></resourcetype>
                    <current-user-privilege-set><privilege><read/></privilege></current-user-privilege-set>
                    <C:supported-calendar-component-set><C:comp name="VEVENT"/></C:supported-calendar-component-set>
                    """)
                + "<response><href>/ben/personal/</href><propstat><prop><displayname/></prop><status>HTTP/1.1 404 Not Found</status></propstat></response>");
        return server;
    }

    private static Task<IReadOnlyList<DiscoveredCollection>> Discover(FakeServer server, string url = Server + "/") =>
        new CalDavDiscovery(new HttpClient(server)).DiscoverAsync(new Uri(url), "ben", "secret");

    [Fact]
    public async Task ServerRoot_FollowsWellKnownAndListsCollections()
    {
        var server = Radicale();
        var found = await Discover(server);

        Assert.Equal(
            [
                new DiscoveredCollection($"{Server}/ben/personal/", "Personal", CollectionKind.Calendar, false),
                new DiscoveredCollection($"{Server}/ben/personal/", "Personal", CollectionKind.TaskList, false),
                new DiscoveredCollection($"{Server}/ben/holidays/", "holidays", CollectionKind.Calendar, true),
            ],
            found);
        Assert.All(server.Requests, r => Assert.Equal("PROPFIND", r.Method.Method));
        Assert.All(server.Requests, r => Assert.Equal("Basic", r.Headers.Authorization?.Scheme));
    }

    [Fact]
    public async Task PrincipalUrl_IsUsedDirectly()
    {
        var server = Radicale();
        server.Routes.Remove("/.well-known/caldav");
        server.Routes["/ben/"] = WithPrincipal(server.Routes["/ben/"]);

        var found = await Discover(server, Server + "/ben/");

        Assert.Equal(3, found.Count);
        Assert.Equal("/ben/", server.Requests[0].RequestUri!.AbsolutePath);
    }

    /// <summary>The principal answers current-user-principal with itself at Depth 0, as Radicale does.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> WithPrincipal(Func<HttpRequestMessage, HttpResponseMessage> inner) =>
        r => r.Headers.GetValues("Depth").Single() == "0" && r.Content!.ReadAsStringAsync().Result.Contains("current-user-principal")
            ? MultiStatus(Ok("/ben/", "<current-user-principal><href>/ben/</href></current-user-principal>"))
            : inner(r);

    [Fact]
    public async Task MissingComponentSet_MeansEventsAndTasks()
    {
        var server = Radicale();
        server.Routes["/ben/"] = r => r.Headers.GetValues("Depth").Single() == "0"
            ? MultiStatus(Ok("/ben/", "<C:calendar-home-set><href>/ben/</href></C:calendar-home-set>"))
            : MultiStatus(Ok("/ben/work/", "<resourcetype><collection/><C:calendar/></resourcetype>"));

        var found = await Discover(server);

        Assert.Equal([CollectionKind.Calendar, CollectionKind.TaskList], found.Select(c => c.Kind));
        Assert.All(found, c => Assert.False(c.ReadOnly));
    }

    [Fact]
    public async Task PlainHttp_IsRejectedWithoutARequest()
    {
        var server = Radicale();
        var ex = await Assert.ThrowsAsync<CalDavDiscoveryException>(() => Discover(server, "http://dav.example.test/"));

        Assert.Equal(DiscoveryError.NotHttps, ex.Error);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Unauthorized_IsWrongCredentials()
    {
        var server = Radicale();
        server.Routes["/"] = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<CalDavDiscoveryException>(() => Discover(server));
        Assert.Equal(DiscoveryError.WrongCredentials, ex.Error);
    }

    [Fact]
    public async Task UntrustedCertificate_IsReported()
    {
        var server = new FakeServer { Throw = new HttpRequestException("ssl", new AuthenticationException("untrusted root")) };

        var ex = await Assert.ThrowsAsync<CalDavDiscoveryException>(() => Discover(server));
        Assert.Equal(DiscoveryError.CertificateNotTrusted, ex.Error);
    }

    [Fact]
    public async Task ConnectionFailure_IsUnreachable()
    {
        var server = new FakeServer { Throw = new HttpRequestException("No such host is known.") };

        var ex = await Assert.ThrowsAsync<CalDavDiscoveryException>(() => Discover(server));
        Assert.Equal(DiscoveryError.Unreachable, ex.Error);
    }

    [Fact]
    public async Task NonCalDavServer_IsReported()
    {
        var ex = await Assert.ThrowsAsync<CalDavDiscoveryException>(() => Discover(new FakeServer()));
        Assert.Equal(DiscoveryError.NotCalDav, ex.Error);
    }

    [Fact]
    public async Task HomeWithoutCalendars_IsNoCalendars()
    {
        var server = Radicale();
        server.Routes["/ben/"] = r => r.Headers.GetValues("Depth").Single() == "0"
            ? MultiStatus(Ok("/ben/", "<C:calendar-home-set><href>/ben/</href></C:calendar-home-set>"))
            : MultiStatus(Ok("/ben/", "<resourcetype><collection/><principal/></resourcetype>"));

        var ex = await Assert.ThrowsAsync<CalDavDiscoveryException>(() => Discover(server));
        Assert.Equal(DiscoveryError.NoCalendars, ex.Error);
    }

    [Fact]
    public async Task RedirectToAnotherHost_IsNotFollowed()
    {
        var server = Radicale();
        server.Routes["/.well-known/caldav"] = _ => Redirect("https://evil.example.test/");
        server.Routes.Remove("/");

        var ex = await Assert.ThrowsAsync<CalDavDiscoveryException>(() => Discover(server));
        Assert.Equal(DiscoveryError.NotCalDav, ex.Error);
        Assert.DoesNotContain(server.Requests, r => r.RequestUri!.Host == "evil.example.test");
    }

    [Fact]
    public void ErrorMessage_HasNoCredentials()
    {
        var ex = new CalDavDiscoveryException(DiscoveryError.WrongCredentials, HttpStatusCode.Unauthorized);
        Assert.Equal("CalDAV discovery failed: WrongCredentials (HTTP 401)", ex.Message);
    }
}
