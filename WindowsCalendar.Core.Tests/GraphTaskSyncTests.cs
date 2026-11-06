using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Extensions.Time.Testing;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>Microsoft To Do (sync-ms365 third slice): mapping, list discovery, tasks delta, removals, writes, conflicts.</summary>
public sealed class GraphTaskSyncTests : IDisposable
{
    /// <summary>
    /// A minimal Graph with one empty calendar and To Do lists: tasks with a version each, tasks delta with tokens that
    /// remember what the list held, and single-task GET / POST / PATCH / DELETE with If-Match.
    /// </summary>
    private sealed class FakeTodo : HttpMessageHandler
    {
        private readonly Dictionary<string, (string List, int Version, HashSet<string> Ids)> _tokens = [];
        private int _created;

        public List<(string Id, string Name, string? WellKnown)> Lists { get; } = [("l1", "Tasks", "defaultList")];
        public Dictionary<string, (string List, string Json, int Version)> Tasks { get; } = [];
        public List<(string Method, string Path, string? Body, string? IfMatch)> Writes { get; } = [];
        public List<(string Path, string Prefer)> Requests { get; } = [];
        public int Version { get; private set; }
        public bool ListsFail { get; set; }
        public bool GoneOnce { get; set; }

        public void Put(string id, string json, string list = "l1")
        {
            Version++;
            var node = JsonNode.Parse(json)!.AsObject();
            node["id"] = id;
            node["@odata.etag"] = $"W/\"t{Version}\"";
            Tasks[id] = (list, node.ToJsonString(), Version);
        }

        public void Delete(string id)
        {
            Version++;
            Tasks.Remove(id);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Tasks)
            {
                return Task.FromResult(Handle(request));
            }
        }

        private HttpResponseMessage Handle(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            Requests.Add((path, string.Join(";", request.Headers.TryGetValues("Prefer", out var p) ? p : [])));
            var query = HttpUtility.ParseQueryString(uri.Query);
            if (path == "/v1.0/me/calendars")
            {
                return Json("""{"value":[{"id":"cal1","name":"Calendar","canEdit":true,"isDefaultCalendar":true}]}""");
            }
            if (path.EndsWith("/calendarView/delta", StringComparison.Ordinal))
            {
                return Json("""{"value":[],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/calendars/cal1/calendarView/delta?$deltatoken=x"}""");
            }
            if (path == "/v1.0/me/todo/lists")
            {
                if (ListsFail)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
                var lists = string.Join(",", Lists.Select(l =>
                    $$"""{"id":"{{l.Id}}","displayName":"{{l.Name}}","wellknownListName":"{{l.WellKnown ?? "none"}}"}"""));
                return Json($$"""{"value":[{{lists}}]}""");
            }
            var parts = path.Split('/');
            // /v1.0/me/todo/lists/{list}/tasks[/delta | /{id}]
            if (parts.Length < 7 || parts[3] != "todo" || parts[6] != "tasks")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            var list = parts[5];
            if (parts.Length == 8 && parts[7] == "delta")
            {
                return Delta(list, query["$deltatoken"]);
            }
            var body = request.Content?.ReadAsStringAsync().Result;
            var ifMatch = request.Headers.TryGetValues("If-Match", out var m) ? m.Single() : null;
            if (parts.Length == 7 && request.Method == HttpMethod.Post)
            {
                Writes.Add(("POST", path, body, ifMatch));
                var id = $"new{++_created}";
                Put(id, body!, list);
                return Json(Tasks[id].Json, HttpStatusCode.Created);
            }
            var taskId = parts[7];
            if (request.Method == HttpMethod.Get)
            {
                return Tasks.TryGetValue(taskId, out var found) ? Json(found.Json) : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            Writes.Add((request.Method.Method, path, body, ifMatch));
            if (!Tasks.TryGetValue(taskId, out var current))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            if (ifMatch is not null && JsonNode.Parse(current.Json)!["@odata.etag"]!.GetValue<string>() != ifMatch)
            {
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            }
            if (request.Method == HttpMethod.Delete)
            {
                Delete(taskId);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            var merged = JsonNode.Parse(current.Json)!.AsObject();
            foreach (var (name, value) in JsonNode.Parse(body!)!.AsObject())
            {
                merged[name] = value?.DeepClone();
            }
            Put(taskId, merged.ToJsonString(), current.List);
            return Json(Tasks[taskId].Json);
        }

        private HttpResponseMessage Delta(string list, string? token)
        {
            var now = Tasks.Where(t => t.Value.List == list).Select(t => t.Key).ToHashSet();
            List<string> entries;
            if (token is not null)
            {
                if (GoneOnce)
                {
                    GoneOnce = false;
                    return new HttpResponseMessage(HttpStatusCode.Gone);
                }
                var t = _tokens[token];
                entries = now.Where(id => Tasks[id].Version > t.Version).Select(id => Tasks[id].Json)
                    .Concat(t.Ids.Where(id => !now.Contains(id)).Select(id => $$"""{"id":"{{id}}","@removed":{"reason":"deleted"} }"""))
                    .ToList();
            }
            else
            {
                entries = now.Select(id => Tasks[id].Json).ToList();
            }
            var next = Guid.NewGuid().ToString("N");
            _tokens[next] = (list, Version, now);
            return Json($$"""{"value":[{{string.Join(",", entries)}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/todo/lists/{{list}}/tasks/delta?$deltatoken={{next}}"}""");
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private const string Task1 = """
        {"title":"Buy milk","status":"notStarted","importance":"high","body":{"content":"2 litres","contentType":"text"},
         "dueDateTime":{"dateTime":"2026-11-10T00:00:00.0000000","timeZone":"UTC"},"lastModifiedDateTime":"2026-11-01T08:00:00Z"}
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeTodo _graph = new();
    private readonly LocalStore _store;
    private readonly Account _account = new("a1", AccountType.MS365, "Work", Username: "ben@example.test", Email: "ben@example.test");
    private readonly GraphSync _sync;

    public GraphTaskSyncTests()
    {
        Directory.CreateDirectory(_dir);
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _store = LocalStore.Open(Path.Combine(_dir, "store.db"), _time);
        _store.AddDiscoveredAccount(_account, [new DiscoveredCollection("cal1", "Calendar", CollectionKind.Calendar, false, true)]);
        _sync = new GraphSync(_store, new HttpClient(_graph), _time);
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private Collection List(string remote = "l1") => _store.GetCollections("a1").Single(c => c.RemoteRef == remote);

    private Item Stored(string remoteId) => _store.FindItemByRemoteId(List().Id, remoteId)!;

    private Task<Sync.SyncSummary> SyncAsync() => _sync.SyncAccountAsync(_account, "token-1");

    private static readonly TimeZoneInfo Singapore = TimeZoneInfo.FindSystemTimeZoneById("Asia/Singapore");

    [Fact]
    public void Map_DueIsTheDatePartInTheWindowsTimezone_AndPayloadIsKept()
    {
        var json = Task1.Replace("2026-11-10T00:00:00.0000000\",\"timeZone\":\"UTC", "2026-11-09T16:00:00.0000000\",\"timeZone\":\"UTC")
            .Replace("\"title\"", "\"id\":\"t1\",\"@odata.etag\":\"W/\\\"e1\\\"\",\"title\"");
        var item = GraphTaskMapper.Map(JsonDocument.Parse(json).RootElement, "c1", Singapore)!;

        Assert.Equal(ItemKind.Task, item.Kind);
        Assert.Equal("t1", item.RemoteId);
        Assert.Equal("W/\"e1\"", item.Etag);
        Assert.Equal("Buy milk", item.Summary);
        Assert.True(item.AllDay);
        // The date part (9 Nov), as midnight in Singapore: no shift by the zone it was written in.
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 16, 0, 0, TimeSpan.Zero), item.DueUtc);
        Assert.Null(item.CompletedUtc);
        Assert.Equal(json, item.RawPayload);

        var fields = GraphTaskMapper.Read(item);
        Assert.Equal(new TaskFields("Buy milk", false, new DateOnly(2026, 11, 9), "high", "2 litres"), fields);
    }

    [Fact]
    public void Map_CompletedAndRecurring()
    {
        var json = """
            {"id":"t2","title":"Water plants","status":"completed","importance":"normal",
             "completedDateTime":{"dateTime":"2026-11-04T00:00:00.0000000","timeZone":"UTC"},
             "recurrence":{"pattern":{"type":"weekly","interval":1,"daysOfWeek":["monday"]},"range":{"type":"noEnd","startDate":"2026-11-02"}}}
            """;
        var item = GraphTaskMapper.Map(JsonDocument.Parse(json).RootElement, "c1", Singapore)!;

        Assert.Equal(new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero), item.CompletedUtc);
        Assert.True(item.IsRecurring);
        Assert.False(item.AllDay);
        Assert.Null(item.DueUtc);
        Assert.True(GraphTaskMapper.Read(item).Completed);
    }

    [Fact]
    public async Task Discovery_AddsListsAndFlaggedEmailsIsReadOnly()
    {
        _graph.Lists.Add(("l2", "Flagged email", "flaggedEmails"));
        var summary = await SyncAsync();

        Assert.False(List("l1").ReadOnly);
        Assert.Equal(CollectionKind.TaskList, List("l1").Kind);
        Assert.True(List("l2").ReadOnly);
        Assert.Equal(2, summary.CalendarListChanges);
        Assert.Equal(List("l1").Id, _store.DefaultTaskListId);
    }

    [Fact]
    public async Task ListsUnavailable_KeepsExistingListsAndSyncsCalendars()
    {
        _graph.Put("t1", Task1);
        await SyncAsync();
        _graph.ListsFail = true;
        string? reported = null;
        _sync.ListsUnavailable += id => reported = id;

        await SyncAsync();

        Assert.Equal("a1", reported);
        Assert.NotNull(Stored("t1"));
        Assert.Contains(_graph.Requests, r => r.Path.EndsWith("/calendarView/delta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pull_StoresTasks_ThenAppliesChangesAndRemovals_WithoutUtcPreference()
    {
        _graph.Put("t1", Task1);
        _graph.Put("t2", """{"title":"No date","status":"notStarted","importance":"normal"}""");
        await SyncAsync();
        Assert.Equal("Buy milk", Stored("t1").Summary);
        Assert.NotNull(Stored("t2"));

        _graph.Put("t1", Task1.Replace("Buy milk", "Buy oat milk"));
        _graph.Delete("t2");
        var summary = await SyncAsync();

        Assert.Equal("Buy oat milk", Stored("t1").Summary);
        Assert.Null(_store.FindItemByRemoteId(List().Id, "t2"));
        Assert.Equal(1, summary.Removed);
        Assert.All(_graph.Requests.Where(r => r.Path.Contains("/todo/", StringComparison.Ordinal)),
            r => Assert.DoesNotContain("outlook.timezone", r.Prefer, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExpiredDelta_RestartsFresh_AndRemovesWhatItNoLongerReturns()
    {
        _graph.Put("t1", Task1);
        _graph.Put("t2", """{"title":"Gone","status":"notStarted"}""");
        await SyncAsync();
        _graph.Delete("t2");
        _graph.GoneOnce = true;

        var summary = await SyncAsync();

        Assert.Equal(1, summary.FullResyncs);
        Assert.NotNull(Stored("t1"));
        Assert.Null(_store.FindItemByRemoteId(List().Id, "t2"));
    }

    [Fact]
    public async Task DueTask_IsPlacedOnItsDueDay()
    {
        _graph.Put("t1", Task1);
        await SyncAsync();

        var views = _store.GetOccurrenceViews(new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero));
        var task = Assert.Single(views, v => v.Kind == ItemKind.Task);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero), task.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero), task.EndUtc);
        Assert.True(task.AllDay);
        Assert.False(task.Completed);
        Assert.Single(_store.GetTasks());
    }

    [Fact]
    public async Task Create_IsPostedToTheList_AndGetsItsGraphId()
    {
        await SyncAsync();
        var created = GraphTasks.Create(List(), new TaskFields("Call Sam", false, new DateOnly(2026, 11, 12), "normal", null), TimeZoneInfo.Utc);
        _store.CreateItem(created);

        await SyncAsync();

        var post = Assert.Single(_graph.Writes);
        Assert.Equal("/v1.0/me/todo/lists/l1/tasks", post.Path);
        var body = JsonNode.Parse(post.Body!)!;
        Assert.Equal("Call Sam", body["title"]!.GetValue<string>());
        Assert.Equal("2026-11-12T00:00:00", body["dueDateTime"]!["dateTime"]!.GetValue<string>());
        Assert.Null(body[GraphEvents.ChangedKey]);
        Assert.Equal("new1", _store.GetItem(created.Id)!.RemoteId);
    }

    [Fact]
    public async Task Complete_PatchesOnlyStatus_WithIfMatch()
    {
        _graph.Put("t1", Task1);
        await SyncAsync();
        var item = Stored("t1");
        var fields = GraphTaskMapper.Read(item) with { Completed = true };
        var edited = GraphTasks.Edit(item, fields, TimeZoneInfo.Utc)!;
        _store.UpdateItem(edited);
        Assert.True(_store.GetOccurrenceViews(new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero)).Single(v => v.Kind == ItemKind.Task).Completed);

        await SyncAsync();

        var patch = Assert.Single(_graph.Writes);
        Assert.Equal("PATCH", patch.Method);
        Assert.Equal("/v1.0/me/todo/lists/l1/tasks/t1", patch.Path);
        Assert.Equal(item.Etag, patch.IfMatch);
        Assert.Equal("""{"status":"completed"}""", patch.Body);
        Assert.Contains("\"status\":\"completed\"", _graph.Tasks["t1"].Json, StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_ListsChangedFields_ClearedDueIsNull_NothingChangedIsNull()
    {
        var item = GraphTaskMapper.Map(JsonDocument.Parse(Task1.Replace("\"title\"", "\"id\":\"t1\",\"title\"")).RootElement, "c1", TimeZoneInfo.Utc)!;
        var same = GraphTaskMapper.Read(item);
        Assert.Null(GraphTasks.Edit(item, same, TimeZoneInfo.Utc));

        var edited = GraphTasks.Edit(item, same with { Due = null, Importance = "low" }, TimeZoneInfo.Utc)!;
        Assert.Equal("""{"dueDateTime":null,"importance":"low"}""", GraphEvents.PatchBody(edited.RawPayload));
        Assert.Null(edited.DueUtc);
        Assert.False(edited.AllDay);
        Assert.Equal(ItemKind.Task, edited.Kind);
    }

    [Fact]
    public async Task EditOfATaskChangedInToDo_IsAConflict_ServerWins()
    {
        _graph.Put("t1", Task1);
        await SyncAsync();
        var item = Stored("t1");
        _store.UpdateItem(GraphTasks.Edit(item, GraphTaskMapper.Read(item) with { Title = "Mine" }, TimeZoneInfo.Utc)!);
        _graph.Put("t1", Task1.Replace("Buy milk", "Theirs"));

        var summary = await SyncAsync();

        Assert.Equal(1, summary.Conflicts);
        Assert.Equal("Theirs", Stored("t1").Summary);
        Assert.Equal(1, _store.GetConflictCount());
    }

    [Fact]
    public async Task Delete_IsSent_AndTheTaskIsGone()
    {
        _graph.Put("t1", Task1);
        await SyncAsync();
        _store.DeleteItem(Stored("t1").Id);

        await SyncAsync();

        var delete = Assert.Single(_graph.Writes);
        Assert.Equal("DELETE", delete.Method);
        Assert.False(_graph.Tasks.ContainsKey("t1"));
        Assert.Null(_store.FindItemByRemoteId(List().Id, "t1"));
    }
}
