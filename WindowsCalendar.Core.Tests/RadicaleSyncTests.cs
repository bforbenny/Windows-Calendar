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
            var body = XDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).Root!;

            if (request.Method.Method == "PROPFIND")
            {
                return MultiStatus(Ok(CollectionPath, $"<sync-token>{Token}</sync-token>"), "");
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

    private Task<SyncSummary> Sync() => new RadicaleSync(_store, new HttpClient(_server), _time).SyncAccountAsync(_account, "secret");

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
    }

    [Fact]
    public async Task UnchangedToken_SkipsCollection()
    {
        _server.Put("e1.ics", Event("e1", "20261110T090000Z"));
        await Sync();
        _server.Methods.Clear();

        var summary = await Sync();

        Assert.Equal(1, summary.Unchanged);
        Assert.Equal(["PROPFIND"], _server.Methods);
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
    public async Task ItemWithUnsentLocalChange_KeepsLocalVersion()
    {
        _server.Put("e1.ics", Event("e1", "20261110T090000Z"));
        await Sync();
        var local = _store.GetItem(Assert.Single(AllOccurrences()).ItemId)!;
        _store.UpdateItem(local with { Summary = "Mine" });
        _server.Commit();
        _server.Put("e1.ics", Event("e1", "20261110T090000Z", "Theirs"));

        var summary = await Sync();

        Assert.Equal(1, summary.KeptLocal);
        Assert.Equal("Mine", _store.GetItem(local.Id)!.Summary);
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

        var ex = await Assert.ThrowsAsync<CalDavException>(Sync);
        Assert.Equal(CalDavError.WrongCredentials, ex.Error);
    }
}
