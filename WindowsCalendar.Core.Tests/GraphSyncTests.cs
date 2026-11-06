using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Time.Testing;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>Pull-only MS365 sync (sync-ms365 slice 1): mapping, chunked delta, removals, loop guard, errors.</summary>
public sealed class GraphSyncTests : IDisposable
{
    /// <summary>
    /// A minimal Graph: events with a version each, calendarView delta per window with tokens that remember what the
    /// window held, pages of <see cref="PageSize"/>, and switches for the failure cases.
    /// </summary>
    private sealed class FakeGraph : HttpMessageHandler
    {
        private readonly Dictionary<string, (DateTimeOffset Start, DateTimeOffset End, int Version, HashSet<string> Ids)> _tokens = [];
        private readonly Dictionary<string, List<string>> _pages = [];
        private readonly Dictionary<string, string?> _pageEnds = [];

        public Dictionary<string, (DateTimeOffset Start, DateTimeOffset End, string Json, int Version)> Events { get; } = [];
        public List<(string Id, string Name, bool CanEdit, bool IsDefault)> Calendars { get; } = [("cal1", "Calendar", true, true)];
        public List<string> Requests { get; } = [];
        public List<string?> Authorizations { get; } = [];
        public int Version { get; private set; }
        public int PageSize { get; set; } = 50;
        public bool RepeatPages { get; set; }
        public bool GoneOnce { get; set; }
        public bool Unauthorized { get; set; }
        public TimeSpan? Throttle { get; set; }

        public void Put(string id, DateTimeOffset start, DateTimeOffset end, string subject = "Meeting", string extra = "")
        {
            Version++;
            var json = $$"""
                {"@odata.etag":"W/\"v{{Version}}\"","id":"{{id}}","subject":"{{subject}}","type":"singleInstance",
                 "isAllDay":false,"isCancelled":false,{{extra}}
                 "start":{"dateTime":"{{start.UtcDateTime:yyyy-MM-ddTHH:mm:ss}}.0000000","timeZone":"UTC"},
                 "end":{"dateTime":"{{end.UtcDateTime:yyyy-MM-ddTHH:mm:ss}}.0000000","timeZone":"UTC"},
                 "location":{"displayName":"Room 1"} }
                """;
            Events[id] = (start, end, json, Version);
        }

        public void Delete(string id)
        {
            Version++;
            Events.Remove(id);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.AbsolutePath + uri.Query);
            Authorizations.Add(request.Headers.Authorization?.ToString());
            if (Unauthorized)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }
            if (Throttle is { } wait)
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
                return Task.FromResult(throttled);
            }
            var query = HttpUtility.ParseQueryString(uri.Query);
            if (uri.AbsolutePath == "/v1.0/me/calendars")
            {
                var list = string.Join(",", Calendars.Select(c =>
                    $$"""{"id":"{{c.Id}}","name":"{{c.Name}}","canEdit":{{(c.CanEdit ? "true" : "false")}},"isDefaultCalendar":{{(c.IsDefault ? "true" : "false")}}}"""));
                return Json($$"""{"value":[{{list}}]}""");
            }
            if (uri.AbsolutePath == "/v1.0/fake/page")
            {
                return Page(query["p"]!);
            }
            if (uri.AbsolutePath.EndsWith("/calendarView/delta", StringComparison.Ordinal))
            {
                List<string> entries;
                DateTimeOffset start, end;
                if (query["$deltatoken"] is { } token)
                {
                    if (GoneOnce)
                    {
                        GoneOnce = false;
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone));
                    }
                    var t = _tokens[token];
                    (start, end) = (t.Start, t.End);
                    var now = Overlapping(start, end);
                    entries = now.Where(id => Events[id].Version > t.Version).Select(id => Events[id].Json)
                        .Concat(t.Ids.Where(id => !now.Contains(id)).Select(Removed))
                        .ToList();
                }
                else
                {
                    start = DateTimeOffset.Parse(query["startDateTime"]!);
                    end = DateTimeOffset.Parse(query["endDateTime"]!);
                    entries = Overlapping(start, end).Select(id => Events[id].Json).ToList();
                }
                var next = Guid.NewGuid().ToString("N");
                _tokens[next] = (start, end, Version, Overlapping(start, end));
                var key = Guid.NewGuid().ToString("N");
                _pages[key] = entries;
                _pageEnds[key] = next;
                return Page(key);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private HashSet<string> Overlapping(DateTimeOffset start, DateTimeOffset end) =>
            Events.Where(e => e.Value.Start < end && e.Value.End > start).Select(e => e.Key).ToHashSet();

        private static string Removed(string id) => $$"""{"@odata.type":"#microsoft.graph.event","id":"{{id}}","@removed":{"reason":"deleted"} }""";

        private Task<HttpResponseMessage> Page(string key)
        {
            var entries = _pages[key];
            var page = entries.Take(PageSize).ToList();
            var value = "[" + string.Join(",", page) + "]";
            if (RepeatPages && entries.Count > 0)
            {
                // Paging forever: the same page again and again.
                return Json($$"""{"value":{{value}},"@odata.nextLink":"https://graph.microsoft.com/v1.0/fake/page?p={{key}}"}""");
            }
            var rest = entries.Skip(PageSize).ToList();
            if (rest.Count > 0)
            {
                var more = Guid.NewGuid().ToString("N");
                _pages[more] = rest;
                _pageEnds[more] = _pageEnds[key];
                return Json($$"""{"value":{{value}},"@odata.nextLink":"https://graph.microsoft.com/v1.0/fake/page?p={{more}}"}""");
            }
            return Json($$"""{"value":{{value}},"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/calendars/cal1/calendarView/delta?$deltatoken={{_pageEnds[key]}}"}""");
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static readonly DateTimeOffset Nov10 = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeGraph _graph = new();
    private readonly LocalStore _store;
    private readonly Account _account = new("a1", AccountType.MS365, "Work", Username: "ben@example.test", Email: "ben@example.test");
    private readonly GraphSync _sync;

    public GraphSyncTests()
    {
        Directory.CreateDirectory(_dir);
        _store = LocalStore.Open(Path.Combine(_dir, "store.db"), _time);
        _store.AddDiscoveredAccount(_account, [new DiscoveredCollection("cal1", "Calendar", CollectionKind.Calendar, false, true)]);
        _sync = new GraphSync(_store, new HttpClient(_graph), _time);
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string CalendarId => _store.GetCollections("a1").Single(c => c.RemoteRef == "cal1").Id;

    private IReadOnlyCollection<string> Cached() => _store.GetRemoteEtags([CalendarId]).Keys.ToList();

    private Task<Sync.SyncSummary> SyncAsync() => _sync.SyncAccountAsync(_account, "token-1");

    [Fact]
    public void Chunks_CoverTheWindowIn30DaySteps()
    {
        var chunks = GraphSync.Chunks(new DateOnly(2026, 11, 5));

        Assert.True(chunks[0] <= new DateOnly(2026, 8, 5) && chunks[0].AddDays(30) > new DateOnly(2026, 8, 5));
        Assert.True(chunks[^1] < new DateOnly(2027, 11, 5) && chunks[^1].AddDays(30) >= new DateOnly(2027, 11, 5));
        Assert.All(chunks.Zip(chunks.Skip(1)), p => Assert.Equal(30, p.Second.DayNumber - p.First.DayNumber));
        // The same chunks the next day.
        Assert.Equal(chunks[1], GraphSync.Chunks(new DateOnly(2026, 11, 6))[1]);
    }

    [Fact]
    public async Task FirstSync_PullsEventsAcrossChunksAndPages()
    {
        _graph.PageSize = 1;
        _graph.Put("e1", Nov10, Nov10.AddHours(1));
        _graph.Put("e2", Nov10.AddHours(2), Nov10.AddHours(3));
        _graph.Put("e3", Nov10.AddDays(100), Nov10.AddDays(100).AddHours(1));

        var summary = await SyncAsync();

        Assert.Equal(["e1", "e2", "e3"], Cached().Order());
        Assert.Equal(3, summary.Fetched);
        Assert.All(_graph.Authorizations, a => Assert.Equal("Bearer token-1", a));
        var view = _store.GetOccurrenceViews(Nov10.AddDays(-1), Nov10.AddDays(1)).Single(o => o.Summary == "Meeting" && o.StartUtc == Nov10);
        Assert.Equal("Room 1", view.Location);
        Assert.Equal(Nov10.AddHours(1), view.EndUtc);
    }

    [Fact]
    public async Task NextSync_AppliesChangesAndRemovals()
    {
        _graph.Put("e1", Nov10, Nov10.AddHours(1));
        _graph.Put("e2", Nov10.AddHours(2), Nov10.AddHours(3));
        await SyncAsync();

        _graph.Put("e1", Nov10, Nov10.AddHours(1), subject: "Renamed");
        _graph.Delete("e2");
        var summary = await SyncAsync();

        Assert.Equal(["e1"], Cached());
        Assert.Equal(1, summary.Fetched);
        Assert.Equal(1, summary.Removed);
        Assert.Contains(_store.GetOccurrenceViews(Nov10.AddDays(-1), Nov10.AddDays(1)), o => o.Summary == "Renamed");
    }

    [Fact]
    public async Task EventInTwoChunks_StaysUntilNoChunkHoldsIt()
    {
        // Find a chunk boundary and put an event across it.
        var boundary = new DateTimeOffset(GraphSync.Chunks(new DateOnly(2026, 11, 5))[4].ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        _graph.Put("span", boundary.AddHours(-1), boundary.AddHours(1));
        await SyncAsync();

        // Moved fully into the later chunk: the earlier one reports it removed, the later one still holds it.
        _graph.Put("span", boundary.AddHours(1), boundary.AddHours(2));
        await SyncAsync();
        Assert.Equal(["span"], Cached());

        _graph.Delete("span");
        await SyncAsync();
        Assert.Empty(Cached());
    }

    [Fact]
    public async Task CancelledEvent_IsRemoved()
    {
        _graph.Put("e1", Nov10, Nov10.AddHours(1));
        await SyncAsync();

        _graph.Put("e1", Nov10, Nov10.AddHours(1), extra: "\"isCancelled\":true,");
        await SyncAsync();

        Assert.Empty(Cached());
    }

    [Fact]
    public async Task RepeatedPages_StopAndRestartWithoutLooping()
    {
        _graph.Put("e1", Nov10, Nov10.AddHours(1));
        _graph.RepeatPages = true;
        var restarts = new List<string>();
        _sync.ChunkRestarted += (_, _, reason) => restarts.Add(reason);

        await SyncAsync();

        Assert.Contains("repeated page", restarts);
        // One restart per chunk at most, never hundreds of pages.
        Assert.True(_graph.Requests.Count(r => r.Contains("fake/page")) <= 4, $"{_graph.Requests.Count} requests");

        // Next run, the server behaves: the chunk starts afresh and completes.
        _graph.RepeatPages = false;
        await SyncAsync();
        Assert.Equal(["e1"], Cached());
    }

    [Fact]
    public async Task ExpiredDeltaState_RestartsTheChunk()
    {
        _graph.Put("e1", Nov10, Nov10.AddHours(1));
        await SyncAsync();
        _graph.Delete("e1");
        _graph.Put("e2", Nov10.AddHours(2), Nov10.AddHours(3));
        _graph.GoneOnce = true;
        var restarts = new List<string>();
        _sync.ChunkRestarted += (_, _, reason) => restarts.Add(reason);

        var summary = await SyncAsync();

        Assert.Equal(["delta state expired"], restarts);
        Assert.Equal(1, summary.FullResyncs);
        // The fresh round did not return e1, so it is gone.
        Assert.Equal(["e2"], Cached());
    }

    [Fact]
    public async Task RollingWindow_DropsEventsThatLeftIt()
    {
        _graph.Put("old", Nov10.AddMonths(-3).AddDays(-1), Nov10.AddMonths(-3).AddDays(-1).AddHours(1));
        _graph.Put("e1", Nov10, Nov10.AddHours(1));
        await SyncAsync();
        Assert.Contains("old", Cached());

        _time.Advance(TimeSpan.FromDays(60));
        await SyncAsync();

        Assert.Equal(["e1"], Cached());
    }

    [Fact]
    public async Task Discovery_FollowsCanEditAndAddsNewCalendars()
    {
        _graph.Calendars.Add(("cal2", "Holidays", false, false));

        var summary = await SyncAsync();

        Assert.Equal(1, summary.CalendarListChanges);
        Assert.True(_store.GetCollections("a1").Single(c => c.RemoteRef == "cal2").ReadOnly);
    }

    [Fact]
    public async Task Throttled_ThrowsWithRetryAfter()
    {
        _graph.Throttle = TimeSpan.FromSeconds(30);

        var ex = await Assert.ThrowsAsync<GraphException>(SyncAsync);

        Assert.Equal(GraphError.Throttled, ex.Error);
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
    }

    [Fact]
    public async Task Unauthorized_Throws()
    {
        _graph.Unauthorized = true;

        var ex = await Assert.ThrowsAsync<GraphException>(SyncAsync);

        Assert.Equal(GraphError.Unauthorized, ex.Error);
        Assert.DoesNotContain("token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Graph event JSON to items (sync-ms365 R8, R12).</summary>
public sealed class GraphEventMapperTests
{
    private static readonly TimeZoneInfo Singapore = TimeZoneInfo.FindSystemTimeZoneById("Asia/Singapore");

    private static Item? Map(string json) => GraphEventMapper.Map(JsonDocument.Parse(json).RootElement, "c1", Singapore);

    [Fact]
    public void Timed_IsUtcAndKeepsPayload()
    {
        const string json = """
            {"@odata.etag":"W/\"abc\"","id":"e1","subject":"Standup","type":"occurrence","isAllDay":false,
             "start":{"dateTime":"2026-11-10T01:00:00.0000000","timeZone":"UTC"},
             "end":{"dateTime":"2026-11-10T01:30:00.0000000","timeZone":"UTC"},"showAs":"busy","categories":["Red"]}
            """;

        var item = Map(json)!;

        Assert.Equal("e1", item.RemoteId);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero), item.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 1, 30, 0, TimeSpan.Zero), item.EndUtc);
        Assert.True(item.IsRecurring);
        Assert.Equal("W/\"abc\"", item.Etag);
        Assert.Contains("\"categories\":[\"Red\"]", item.RawPayload);
    }

    [Fact]
    public void AllDay_IsMidnightInTheWindowsTimezone()
    {
        var item = Map("""
            {"id":"e1","subject":"Holiday","type":"singleInstance","isAllDay":true,
             "start":{"dateTime":"2026-11-10T00:00:00.0000000","timeZone":"UTC"},
             "end":{"dateTime":"2026-11-11T00:00:00.0000000","timeZone":"UTC"}}
            """)!;

        Assert.True(item.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 16, 0, 0, TimeSpan.Zero), item.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 16, 0, 0, TimeSpan.Zero), item.EndUtc);
    }

    [Theory]
    [InlineData("""{"id":"e1","type":"seriesMaster","start":{"dateTime":"2026-11-10T00:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-10T01:00:00","timeZone":"UTC"}}""")]
    [InlineData("""{"id":"e1","type":"singleInstance","isCancelled":true,"start":{"dateTime":"2026-11-10T00:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-10T01:00:00","timeZone":"UTC"}}""")]
    [InlineData("""{"id":"e1","type":"singleInstance"}""")]
    public void NotShown_IsNull(string json) => Assert.Null(Map(json));
}
