using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Time.Testing;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Sync;

namespace WindowsCalendar.Core.Tests;

public sealed class RadicaleSyncTests : IDisposable
{
    private const string CollectionPath = "/ben/personal/";
    private static readonly XNamespace D = "DAV:";

    /// <summary>
    /// A minimal stateful Radicale: resources by path, a sync-token that changes with every Commit(),
    /// and sync-collection answering "everything" (empty token), "since the previous token", or 409.
    /// </summary>
    private sealed class FakeRadicale : HttpMessageHandler
    {
        public Dictionary<string, (string Etag, string Data)> Resources { get; } = [];
        public List<string> Methods { get; } = [];
        public List<int> MultigetSizes { get; } = [];
        public bool Unauthorized { get; set; }
        public int Version { get; private set; } = 1;

        /// <summary>Extra entries in the collection listing (e.g. subscriptions), as PROPFIND response XML.</summary>
        public List<string> ExtraCollections { get; } = [];

        /// <summary>Feed bodies by absolute URL; GETs of them are recorded with their headers.</summary>
        public Dictionary<string, string> Feeds { get; } = [];
        public List<HttpRequestMessage> FeedRequests { get; } = [];

        private HashSet<string> _changed = [];
        private HashSet<string> _removed = [];

        public string Token => $"http://radicale.org/ns/sync/{Version}";

        public void Put(string name, string data)
        {
            var path = CollectionPath + name;
            Resources[path] = ($"\"{Guid.NewGuid():N}\"", data);
            _changed.Add(path);
        }

        public void Delete(string name)
        {
            Resources.Remove(CollectionPath + name);
            _removed.Add(CollectionPath + name);
        }

        /// <summary>Starts a new token; changes made after this are "since the previous token".</summary>
        public void Commit()
        {
            Version++;
            _changed = [];
            _removed = [];
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method.Method);
            if (Unauthorized)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            if (request.Method == HttpMethod.Put || request.Method == HttpMethod.Delete)
            {
                return await WriteAsync(request, cancellationToken);
            }
            if (request.Method == HttpMethod.Get)
            {
                FeedRequests.Add(request);
                return Feeds.TryGetValue(request.RequestUri!.AbsoluteUri, out var feed)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(feed) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            var body = XDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).Root!;

            if (request.Method.Method == "PROPFIND")
            {
                // Discovery (re-read on every sync): principal and home are /ben/; the listing has the collection.
                var prop = body.Element(D + "prop")!.Elements().First().Name.LocalName;
                return prop switch
                {
                    "current-user-principal" => MultiStatus(Ok("/", "<current-user-principal><href>/ben/</href></current-user-principal>"), ""),
                    "calendar-home-set" => MultiStatus(Ok("/ben/", "<C:calendar-home-set><href>/ben/</href></C:calendar-home-set>"), ""),
                    "resourcetype" => MultiStatus(
                        Ok(CollectionPath, "<resourcetype><collection/><C:calendar/></resourcetype><displayname>Personal</displayname>")
                        + string.Concat(ExtraCollections), ""),
                    _ => MultiStatus(Ok(CollectionPath, $"<sync-token>{Token}</sync-token>"), ""),
                };
            }

            if (body.Name.LocalName == "sync-collection")
            {
                var token = body.Element(D + "sync-token")!.Value;
                IEnumerable<string> changed, removed;
                if (token == "")
                {
                    (changed, removed) = (Resources.Keys, []);
                }
                else if (token == $"http://radicale.org/ns/sync/{Version - 1}")
                {
                    (changed, removed) = (_changed.Where(Resources.ContainsKey), _removed);
                }
                else
                {
                    return new HttpResponseMessage(HttpStatusCode.Conflict);
                }
                return MultiStatus(
                    string.Concat(changed.Select(p => Ok(p, $"<getetag>{Resources[p].Etag}</getetag>")))
                    + string.Concat(removed.Select(p => $"<response><href>{p}</href><status>HTTP/1.1 404 Not Found</status></response>")),
                    $"<sync-token>{Token}</sync-token>");
            }

            // calendar-multiget
            var hrefs = body.Elements(D + "href").Select(h => h.Value).ToList();
            MultigetSizes.Add(hrefs.Count);
            return MultiStatus(string.Concat(hrefs.Where(Resources.ContainsKey).Select(p =>
                Ok(p, $"<getetag>{Resources[p].Etag}</getetag><C:calendar-data>{System.Security.SecurityElement.Escape(Resources[p].Data)}</C:calendar-data>"))), "");
        }

        /// <summary>PUT / DELETE with If-Match / If-None-Match, as Radicale does; a PUT answers with the new ETag.</summary>
        private async Task<HttpResponseMessage> WriteAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var exists = Resources.TryGetValue(path, out var current);
            var ifMatch = request.Headers.TryGetValues("If-Match", out var m) ? m.Single() : null;
            var ifNoneMatch = request.Headers.Contains("If-None-Match");
            if (ifNoneMatch && exists || ifMatch is not null && (!exists || current.Etag != ifMatch))
            {
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            }
            if (request.Method == HttpMethod.Delete)
            {
                if (!exists)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
                Delete(path[CollectionPath.Length..]);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            Put(path[CollectionPath.Length..], await request.Content!.ReadAsStringAsync(cancellationToken));
            var response = new HttpResponseMessage(exists ? HttpStatusCode.NoContent : HttpStatusCode.Created);
            response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(Resources[path].Etag);
            return response;
        }

        private static string Ok(string href, string props) =>
            $"<response><href>{href}</href><propstat><prop>{props}</prop><status>HTTP/1.1 200 OK</status></propstat></response>";

        private static HttpResponseMessage MultiStatus(string responses, string tail) => new(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(
                $"""<?xml version="1.0"?><multistatus xmlns="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">{responses}{tail}</multistatus>""",
                Encoding.UTF8, "application/xml"),
        };
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("wincal-sync-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly LocalStore _store;
    private readonly FakeRadicale _server = new();
    private readonly Account _account = new("a1", AccountType.CalDAV, "Radicale", "https://dav.example.test/", "ben");
    private readonly IReadOnlyList<Collection> _collections;

    public RadicaleSyncTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/London"));
        _store = LocalStore.Open(Path.Combine(_dir, "store.db"), _time);
        var url = "https://dav.example.test" + CollectionPath;
        _collections = _store.AddDiscoveredAccount(_account,
            [new(url, "Personal", CollectionKind.Calendar, false), new(url, "Personal", CollectionKind.TaskList, false)]);
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private Task<SyncSummary> Sync(bool forceFeeds = false) =>
        new RadicaleSync(_store, new HttpClient(_server), _time).SyncAccountAsync(_account, "secret", forceFeeds);

    private static string Event(string uid, string start, string summary = "Meeting") =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260101T000000Z\r\n"
        + $"DTSTART:{start}\r\nDURATION:PT1H\r\nSUMMARY:{summary}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    private static string Todo(string uid, string due) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\nBEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260101T000000Z\r\n"
        + $"DUE;VALUE=DATE:{due}\r\nSUMMARY:Task {uid}\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

    private IReadOnlyList<ItemOccurrence> AllOccurrences() =>
        _store.GetOccurrences(DateTimeOffset.MinValue.AddYears(1), DateTimeOffset.MaxValue.AddYears(-1));

    private IReadOnlyDictionary<string, string?> Cached() => _store.GetRemoteEtags(_collections.Select(c => c.Id).ToList());

    [Fact]
    public async Task FirstSync_PutsEventsAndTasksInMatchingCollections()
    {
        _server.Put("e1.ics", Event("e1", "20261110T090000Z"));
        _server.Put("t1.ics", Todo("t1", "20261112"));

        var summary = await Sync();

        Assert.Equal((1, 1, 2), (summary.Collections, summary.FullResyncs, summary.Fetched));
        var occurrence = Assert.Single(AllOccurrences(), o => _store.GetItem(o.ItemId)!.Kind == ItemKind.Event);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero), occurrence.StartUtc);
        var ev = _store.GetItem(occurrence.ItemId)!;
        Assert.Equal((_collections[0].Id, "Meeting"), (ev.CollectionId, ev.Summary));
        Assert.Equal(2, Cached().Count);
        Assert.Empty(_store.GetOutbox());
        Assert.Equal(_server.Token, _store.GetSyncState(_collections[0].Id, "sync-token"));
        Assert.All(_server.Methods, m => Assert.Contains(m, new[] { "PROPFIND", "REPORT" }));
        Assert.Equal(0, summary.CalendarListChanges);
    }

    [Fact]
    public async Task UnchangedToken_SkipsCollection()
    {
        _server.Put("e1.ics", Event("e1", "20261110T090000Z"));
        await Sync();
        _server.Methods.Clear();

        var summary = await Sync();

        Assert.Equal(1, summary.Unchanged);
        // Three for re-discovery (principal, home, listing), one for the sync-token.
        Assert.Equal(["PROPFIND", "PROPFIND", "PROPFIND", "PROPFIND"], _server.Methods);
    }

    [Fact]
    public async Task IncrementalSync_AppliesChangesAndRemovals()
    {
        _server.Put("e1.ics", Event("e1", "20261110T090000Z"));
        _server.Put("t1.ics", Todo("t1", "20261112"));
        await Sync();
        _server.Commit();
        _server.Put("e1.ics", Event("e1", "20261111T090000Z", "Moved"));
        _server.Delete("t1.ics");

        var summary = await Sync();

        Assert.Equal((0, 1, 1), (summary.FullResyncs, summary.Fetched, summary.Removed));
        var ev = _store.GetItem(Assert.Single(AllOccurrences()).ItemId)!;
        Assert.Equal("Moved", ev.Summary);
        Assert.Single(Cached());
    }

    [Fact]
    public async Task RejectedToken_ResyncsFromScratch()
    {
        _server.Put("e1.ics", Event("e1", "20261110T090000Z"));
        _server.Put("e2.ics", Event("e2", "20261111T090000Z"));
        await Sync();
        // Server lost its sync history: two versions on, and e2 deleted.
        _server.Commit();
        _server.Commit();
        _server.Delete("e2.ics");
        _server.MultigetSizes.Clear();

        var summary = await Sync();

        Assert.Equal((1, 0, 1), (summary.FullResyncs, summary.Fetched, summary.Removed));
        Assert.Empty(_server.MultigetSizes); // e1's etag matched; nothing to fetch.
        Assert.Single(Cached());
    }

    [Fact]
    public async Task ConcurrentEdit_ServerWinsAndLocalIsKeptAsConflict()
    {
        _server.Put("e1.ics", Event("e1", "20261110T090000Z"));
        await Sync();
        var local = _store.GetItem(Assert.Single(AllOccurrences()).ItemId)!;
        _store.UpdateItem(local with { RawPayload = Event("e1", "20261110T090000Z", "Mine"), Summary = "Mine" });
        _server.Commit();
        _server.Put("e1.ics", Event("e1", "20261110T090000Z", "Theirs"));

        var summary = await Sync();

        Assert.Equal((1, 0), (summary.Conflicts, summary.Sent));
        Assert.Equal("Theirs", _store.GetItem(local.Id)!.Summary);
        Assert.Equal(1, _store.GetConflictCount());
        Assert.Empty(_store.GetOutbox());
        Assert.Contains("Theirs", _server.Resources[CollectionPath + "e1.ics"].Data);
    }

    [Fact]
    public async Task LocalCreateUpdateAndDelete_ReachTheServer()
    {
        _server.Put("e1.ics", Event("e1", "20261110T090000Z"));
        await Sync();
        var existing = _store.GetItem(Assert.Single(AllOccurrences()).ItemId)!;
        var created = CalDavEvents.Create(_collections[0],
            new EventFields("New", false, new DateTime(2026, 11, 12, 9, 0, 0), new DateTime(2026, 11, 12, 10, 0, 0), null, null),
            _time.LocalTimeZone, _time.GetUtcNow());
        _store.CreateItem(created);
        _store.DeleteItem(existing.Id);

        var summary = await Sync();

        Assert.Equal((2, 0, 0), (summary.Sent, summary.Conflicts, summary.Failed));
        var path = new Uri(created.RemoteId!).AbsolutePath;
        Assert.Equal([path], _server.Resources.Keys);
        Assert.Equal(_server.Resources[path].Etag, _store.GetItem(created.Id)!.Etag);
        Assert.Null(_store.GetItem(existing.Id, includeDeleted: true));
        Assert.Empty(_store.GetOutbox());
        // The pull that follows sees our own write and fetches nothing.
        Assert.Equal(0, summary.Fetched);

        var edited = CalDavEvents.Edit(_store.GetItem(created.Id)!,
            CalDavEvents.Read(_store.GetItem(created.Id)!, _time.LocalTimeZone) with { Title = "Renamed" }, _time.LocalTimeZone, _time.GetUtcNow())!;
        _store.UpdateItem(edited);
        summary = await Sync();

        Assert.Equal((1, 0), (summary.Sent, summary.Conflicts));
        Assert.Contains("SUMMARY:Renamed", _server.Resources[path].Data);
    }

    [Fact]
    public async Task DeleteOfItemAlreadyGone_Completes()
    {
        _server.Put("e1.ics", Event("e1", "20261110T090000Z"));
        await Sync();
        var id = Assert.Single(AllOccurrences()).ItemId;
        _store.DeleteItem(id);
        _server.Resources.Clear();

        var summary = await Sync();

        Assert.Equal((1, 0), (summary.Sent, summary.Conflicts));
        Assert.Empty(_store.GetOutbox());
    }

    [Fact]
    public async Task UnreachableServer_KeepsChangeQueuedWithBackoff()
    {
        _store.CreateItem(CalDavEvents.Create(_collections[0],
            new EventFields("New", false, new DateTime(2026, 11, 12, 9, 0, 0), new DateTime(2026, 11, 12, 10, 0, 0), null, null),
            _time.LocalTimeZone, _time.GetUtcNow()));
        var sync = new RadicaleSync(_store, new HttpClient(new Failing(_server)), _time);

        var ex = await Assert.ThrowsAsync<CalDavException>(() => sync.SyncAccountAsync(_account, "secret", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(CalDavError.Unreachable, ex.Error);
        var entry = Assert.Single(_store.GetOutbox());
        Assert.Equal((1, false), (entry.Attempts, entry.InFlight));
        Assert.Empty(_store.GetDueOutbox());
    }

    private const string Subscription = """
        <response><href>/ben/au-holidays/</href><propstat><prop>
        <resourcetype xmlns:CS="http://calendarserver.org/ns/"><collection/><CS:subscribed/></resourcetype>
        <displayname>AU holidays</displayname>
        <CS:source xmlns:CS="http://calendarserver.org/ns/"><href>webcal://feeds.example.test/au.ics</href></CS:source>
        </prop><status>HTTP/1.1 200 OK</status></propstat></response>
        """;

    private static string Feed(params string[] uids) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//feed//EN\r\n"
        + string.Concat(uids.Select((u, i) => $"BEGIN:VEVENT\r\nUID:{u}\r\nDTSTAMP:20260101T000000Z\r\nDTSTART;VALUE=DATE:2026112{i}\r\nSUMMARY:Holiday {u}\r\nEND:VEVENT\r\n"))
        + "END:VCALENDAR\r\n";

    [Fact]
    public async Task NewSubscription_IsDiscoveredAndItsFeedFetchedWithoutCredentials()
    {
        await Sync();
        _server.ExtraCollections.Add(Subscription);
        _server.Feeds["https://feeds.example.test/au.ics"] = Feed("h1", "h2");

        var summary = await Sync();

        Assert.Equal((1, 2, 0), (summary.CalendarListChanges, summary.FeedItems, summary.FeedFailures));
        var sub = Assert.Single(_store.GetCollections(_account.Id), c => c.SourceUrl is not null);
        Assert.True(sub.ReadOnly);
        Assert.Equal(2, _store.GetRemoteEtags([sub.Id]).Count);
        Assert.Null(Assert.Single(_server.FeedRequests).Headers.Authorization);
    }

    [Fact]
    public async Task Feed_IsRefreshedHourlyUnlessForced_AndDropsRemovedEvents()
    {
        _server.ExtraCollections.Add(Subscription);
        _server.Feeds["https://feeds.example.test/au.ics"] = Feed("h1", "h2");
        await Sync();
        _server.Feeds["https://feeds.example.test/au.ics"] = Feed("h1");

        var soon = await Sync();
        _time.Advance(TimeSpan.FromMinutes(61));
        var later = await Sync();

        Assert.Equal(0, soon.FeedItems);
        Assert.Equal(1, later.FeedItems);
        var sub = Assert.Single(_store.GetCollections(_account.Id), c => c.SourceUrl is not null);
        Assert.Single(_store.GetRemoteEtags([sub.Id]));

        _server.Feeds["https://feeds.example.test/au.ics"] = Feed("h1", "h3");
        Assert.Equal(1, (await Sync(forceFeeds: true)).FeedItems);
    }

    [Fact]
    public async Task CollectionGoneFromServer_IsRemovedAndPendingChangesKept()
    {
        _server.ExtraCollections.Add(Subscription.Replace("CS:subscribed", "C:calendar").Replace("au-holidays", "work"));
        await Sync();
        var work = _store.GetCollections(_account.Id).First(c => c.RemoteRef.EndsWith("/work/") && c.Kind == CollectionKind.Calendar);
        _store.CreateItem(CalDavEvents.Create(work,
            new EventFields("Draft", false, new DateTime(2026, 11, 12, 9, 0, 0), new DateTime(2026, 11, 12, 10, 0, 0), null, null),
            _time.LocalTimeZone, _time.GetUtcNow()));
        _server.ExtraCollections.Clear();

        var summary = await Sync();

        Assert.Equal(2, summary.CalendarListChanges); // Its calendar and task list.
        Assert.DoesNotContain(_store.GetCollections(_account.Id), c => c.Id == work.Id);
        Assert.Equal(1, _store.GetConflictCount());
        Assert.Empty(_store.GetOutbox());
    }

    /// <summary>The connection drops on writes; discovery still answers.</summary>
    private sealed class Failing(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.Method == HttpMethod.Put ? throw new HttpRequestException("offline") : base.SendAsync(request, cancellationToken);
    }

    [Fact]
    public async Task ManyChanges_AreFetchedInBatchesOf100()
    {
        for (var i = 0; i < 250; i++)
        {
            _server.Put($"e{i}.ics", Event($"e{i}", "20261110T090000Z"));
        }

        var summary = await Sync();

        Assert.Equal(250, summary.Fetched);
        Assert.Equal([100, 100, 50], _server.MultigetSizes);
    }

    [Fact]
    public async Task WrongPassword_Throws()
    {
        _server.Unauthorized = true;

        var ex = await Assert.ThrowsAsync<CalDavException>(() => Sync());
        Assert.Equal(CalDavError.WrongCredentials, ex.Error);
    }
}
