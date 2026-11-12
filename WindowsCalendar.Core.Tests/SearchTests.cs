using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>Search tests run alone, so the R8 timing is not measured against other tests' load.</summary>
[CollectionDefinition(nameof(SearchTests), DisableParallelization = true)]
public sealed class SearchTimingCollection;

/// <summary>Offline full-text search (search R1-R3, R6-R8).</summary>
[Collection(nameof(SearchTests))]
public sealed class SearchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wincal-search-").FullName;
    private string DbPath => Path.Combine(_dir, "store.db");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly LocalStore _store;

    public SearchTests()
    {
        _store = LocalStore.Open(DbPath, _time);
        _store.AddAccount(new Account("a1", AccountType.CalDAV, "Radicale", Email: "me@dav.example.test"));
        _store.AddCollection(new Collection("c1", "a1", CollectionKind.Calendar, "Home", "Navy", "Circle", true, false, "https://dav.example.test/home/"));
        _store.AddAccount(new Account("a2", AccountType.MS365, "Work", Email: "me@work.example.test"));
        _store.AddCollection(new Collection("g1", "a2", CollectionKind.Calendar, "Calendar", "Teal", "Circle", true, false, "graph-cal"));
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static Item CalDav(string uid, string summary, string start, string extra = "") =>
        CalDavItemMapper.Map($"https://dav.example.test/home/{uid}.ics", "\"e\"",
            ($"BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Test//EN\nBEGIN:VEVENT\nUID:{uid}\nDTSTAMP:20260101T000000Z\n"
             + $"DTSTART:{start}\nDURATION:PT1H\nSUMMARY:{summary}\n{extra}END:VEVENT\nEND:VCALENDAR\n")
            .Replace("\n", "\r\n"), TimeZoneInfo.Utc)! with { Id = uid, CollectionId = "c1" };

    private static Item Graph(string id, string subject, string start, string extra = "") => GraphEventMapper.Map(JsonDocument.Parse($$"""
        {"id":"{{id}}","subject":"{{subject}}",{{extra}}
         "start":{"dateTime":"{{start}}","timeZone":"UTC"},"end":{"dateTime":"{{start[..^2]}}59","timeZone":"UTC"} }
        """).RootElement, "g1", TimeZoneInfo.Utc)! with { Id = id };

    private static Item Todo(string uid, string summary, string? due = null) =>
        CalDavItemMapper.Map($"https://dav.example.test/home/{uid}.ics", "\"e\"",
            ($"BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Test//EN\nBEGIN:VTODO\nUID:{uid}\nDTSTAMP:20260101T000000Z\n"
             + $"SUMMARY:{summary}\n{(due is null ? "" : $"DUE:{due}\n")}END:VTODO\nEND:VCALENDAR\n")
            .Replace("\n", "\r\n"), TimeZoneInfo.Utc)! with { Id = uid, CollectionId = "c1" };

    private static DateTimeOffset Utc(int month, int day) => new(2026, month, day, 0, 0, 0, TimeSpan.Zero);

    private static string Plain(string marked) =>
        marked.Replace(SearchResult.MarkStart.ToString(), "").Replace(SearchResult.MarkEnd.ToString(), "");

    [Fact]
    public void FindsTitleLocationAndDescription_InBothAccounts()
    {
        _store.CreateItem(CalDav("r1", "Dentist", "20261110T010000Z", "LOCATION:Smile Clinic\nDESCRIPTION:Bring the referral\n"));
        _store.CreateItem(Graph("m1", "Budget review", "2026-11-11T01:00:00",
            """ "location":{"displayName":"Boardroom"},"body":{"contentType":"html","content":"<p>Bring <b>forecasts</b></p>"}, """));

        Assert.Equal(["r1"], _store.Search("dentist").Select(r => r.ItemId));
        Assert.Equal(["r1"], _store.Search("smile").Select(r => r.ItemId));
        Assert.Equal(["r1"], _store.Search("referral").Select(r => r.ItemId));
        Assert.Equal(["m1"], _store.Search("boardroom").Select(r => r.ItemId));
        Assert.Equal(["m1"], _store.Search("forecasts").Select(r => r.ItemId));
        Assert.Equal(["m1", "r1"], _store.Search("bring").Select(r => r.ItemId).Order());
    }

    [Fact]
    public void MatchesPrefixesAndIgnoresAccents_AndEveryWordMustMatch()
    {
        _store.CreateItem(CalDav("r1", "Lunch at Café Rio", "20261110T010000Z"));

        Assert.Single(_store.Search("caf"));
        Assert.Single(_store.Search("cafe ri"));
        Assert.Single(_store.Search("LUN"));
        Assert.Empty(_store.Search("lunch dinner"));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("*")]
    [InlineData("lunch AND (")]
    [InlineData("NEAR(lunch")]
    [InlineData("   ")]
    public void TypedQuerySyntax_IsReadAsWords(string text)
    {
        _store.CreateItem(CalDav("r1", "Lunch", "20261110T010000Z"));

        var ex = Record.Exception(() => _store.Search(text));

        Assert.Null(ex);
    }

    [Fact]
    public void HighlightsTheTitle_AndSnippetsOtherText()
    {
        _store.CreateItem(CalDav("r1", "Dentist", "20261110T010000Z", "DESCRIPTION:Bring the referral letter\n"));

        var title = _store.Search("dent").Single();
        var body = _store.Search("referral").Single();

        Assert.Equal($"{SearchResult.MarkStart}Dentist{SearchResult.MarkEnd}", title.Title);
        Assert.Null(title.Snippet);
        Assert.Equal("Dentist", body.Title);
        Assert.Contains($"{SearchResult.MarkStart}referral{SearchResult.MarkEnd}", body.Snippet);
    }

    [Fact]
    public void EditedItem_IsSearchableByItsNewTextAtOnce()
    {
        var item = CalDav("r1", "Dentist", "20261110T010000Z");
        _store.CreateItem(item);

        _store.UpdateItem(item with { Summary = "Orthodontist" });

        Assert.Empty(_store.Search("dentist"));
        Assert.Single(_store.Search("orthodontist"));
    }

    [Fact]
    public void DeletedItems_AreNotFound()
    {
        _store.ApplyServerItem(["c1"], CalDav("r1", "Dentist", "20261110T010000Z"));
        _store.ApplyServerItem(["c1"], CalDav("r2", "Dentist again", "20261111T010000Z"));

        _store.DeleteItem("r1");
        _store.RemoveServerItem(["c1"], "https://dav.example.test/home/r2.ics");

        Assert.Empty(_store.Search("dentist"));
        using var db = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM search_index;";
        Assert.Equal(1L, cmd.ExecuteScalar()); // The tombstone stays until its delete is sent.
    }

    [Fact]
    public void CalendarName_IsSearchable_AndFollowsARename()
    {
        _store.CreateItem(CalDav("r1", "Dentist", "20261110T010000Z"));
        Assert.Single(_store.Search("home"));

        _store.SyncCollections("a1", [new DiscoveredCollection("https://dav.example.test/home/", "Family", CollectionKind.Calendar, false)]);

        Assert.Empty(_store.Search("home"));
        Assert.Single(_store.Search("family"));
    }

    [Fact]
    public void HiddenCalendars_AreNotSearched_UntilShownAgain()
    {
        _store.CreateItem(CalDav("r1", "Dentist", "20261110T010000Z"));

        _store.SetCollectionHidden("c1", true);
        Assert.Empty(_store.Search("dentist"));

        _store.SetCollectionHidden("c1", false);
        Assert.Single(_store.Search("dentist"));
    }

    [Fact]
    public void ExistingItems_AreIndexedOnOpenAfterTheUpgrade()
    {
        _store.CreateItem(CalDav("r1", "Dentist", "20261110T010000Z"));
        using (var db = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM search_index; INSERT INTO meta (key, value) VALUES ('build-search', '1');";
            cmd.ExecuteNonQuery();
        }
        Assert.Empty(_store.Search("dentist"));

        using var reopened = LocalStore.Open(DbPath, _time);

        Assert.Single(reopened.Search("dentist"));
    }

    [Fact]
    public void GraphAttendeesAndOrganiser_AreSearchable_ByNameAndAddress()
    {
        _store.CreateItem(Graph("m1", "Budget review", "2026-11-11T01:00:00",
            """
            "organizer":{"emailAddress":{"name":"Olivia Organiser","address":"olivia@boss.example.test"}},
            "attendees":[{"emailAddress":{"name":"Priya Patel","address":"priya@client.example.test"}}],
            """));

        Assert.Equal(["m1"], _store.Search("priya").Select(r => r.ItemId));
        Assert.Equal(["m1"], _store.Search("patel").Select(r => r.ItemId));
        Assert.Equal(["m1"], _store.Search("priya@client.example.test").Select(r => r.ItemId));
        Assert.Equal(["m1"], _store.Search("olivia").Select(r => r.ItemId));
        Assert.Equal(["m1"], _store.Search("olivia@boss.example.test").Select(r => r.ItemId));
    }

    [Fact]
    public void CalDavAttendeesAndOrganiser_AreSearchable_ByNameAndAddress()
    {
        _store.CreateItem(CalDav("r1", "Planning", "20261110T010000Z",
            "ORGANIZER;CN=Olivia Organiser:mailto:olivia@boss.example.test\nATTENDEE;CN=Priya Patel:MAILTO:priya@client.example.test\n"));

        Assert.Equal(["r1"], _store.Search("priya").Select(r => r.ItemId));
        Assert.Equal(["r1"], _store.Search("priya@client.example.test").Select(r => r.ItemId));
        Assert.Equal(["r1"], _store.Search("olivia").Select(r => r.ItemId));
        Assert.Equal(["r1"], _store.Search("olivia@boss.example.test").Select(r => r.ItemId));
    }

    [Fact]
    public void TheAccountsOwnEntry_IsNotIndexed_NameIncluded()
    {
        _store.CreateItem(Graph("m1", "Budget review", "2026-11-11T01:00:00",
            """
            "attendees":[{"emailAddress":{"name":"Marlon Myself","address":"me@work.example.test"}},
                         {"emailAddress":{"name":"Priya Patel","address":"priya@client.example.test"}}],
            """));
        _store.CreateItem(CalDav("r1", "Planning", "20261110T010000Z",
            "ATTENDEE;CN=Marlon Myself:mailto:ME@dav.example.test\nATTENDEE;CN=Priya Patel:mailto:priya@client.example.test\n"));

        Assert.Empty(_store.Search("marlon"));
        Assert.Empty(_store.Search("me@work.example.test"));
        Assert.Empty(_store.Search("me@dav.example.test"));
        Assert.Equal(["m1", "r1"], _store.Search("priya").Select(r => r.ItemId).Order());
    }

    [Fact]
    public void People_ReturnsDistinctMatchesFromVisibleNonDeletedItemsOnly()
    {
        _store.AddCollection(new Collection("c2", "a1", CollectionKind.Calendar, "Other", "Navy", "Circle", true, false, "https://dav.example.test/other/"));
        _store.CreateItem(CalDav("r1", "One", "20261110T010000Z",
            "ATTENDEE;CN=Alice Adams:mailto:alice@example.test\nATTENDEE;CN=Bob Brown:mailto:bob@example.test\n"));
        _store.CreateItem(CalDav("r2", "Two", "20261111T010000Z",
            "ATTENDEE;CN=Alice Adams:mailto:alice@example.test\nATTENDEE;CN=Albert Ng:mailto:albert@example.test\n"));
        _store.CreateItem(CalDav("r3", "Three", "20261112T010000Z", "ATTENDEE;CN=Alan Gone:mailto:alan@example.test\n"));
        _store.CreateItem(CalDav("r4", "Four", "20261113T010000Z", "ATTENDEE;CN=Alma Hidden:mailto:alma@example.test\n") with { CollectionId = "c2" });
        _store.DeleteItem("r3");
        _store.SetCollectionHidden("c2", true);

        Assert.Equal(["Albert Ng", "albert@example.test", "Alice Adams", "alice@example.test"], _store.People("al"));
        Assert.Equal(["Alice Adams"], _store.People("adams"));
        Assert.Equal(2, _store.People("al", 2).Count);
        Assert.Empty(_store.People("   "));
        Assert.Empty(_store.People("zz"));
    }

    [Fact]
    public void ExistingItems_GetPeopleIndexedOnOpenAfterTheUpgrade()
    {
        _store.CreateItem(CalDav("r1", "Planning", "20261110T010000Z", "ATTENDEE;CN=Priya Patel:mailto:priya@client.example.test\n"));
        using (var db = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM search_index; INSERT INTO meta (key, value) VALUES ('build-search', '1');";
            cmd.ExecuteNonQuery();
        }
        Assert.Empty(_store.Search("priya"));

        using var reopened = LocalStore.Open(DbPath, _time);

        Assert.Equal(["r1"], reopened.Search("priya").Select(r => r.ItemId));
    }

    [Fact]
    public void Ms365Series_IsOneResult_AtItsNextOccurrence()
    {
        const string series = """ "type":"occurrence","seriesMasterId":"master", """;
        _store.ApplyServerItem(["g1"], Graph("master", "Standup", "2026-10-01T01:00:00",
            """ "type":"seriesMaster","recurrence":{"pattern":{"type":"daily","interval":1},"range":{"type":"noEnd","startDate":"2026-10-01"}}, """)
            with { RemoteId = "master" });
        _store.ApplyServerItem(["g1"], Graph("o1", "Standup", "2026-11-05T01:00:00", series) with { RemoteId = "o1" });
        _store.ApplyServerItem(["g1"], Graph("o2", "Standup", "2026-11-07T01:00:00", series) with { RemoteId = "o2" });
        _store.ApplyServerItem(["g1"], Graph("o3", "Standup", "2026-11-08T01:00:00", series) with { RemoteId = "o3" });

        var result = Assert.Single(_store.Search("standup"));

        Assert.Equal("o2", result.ItemId);
        Assert.True(result.IsSeries);
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 1, 0, 0, TimeSpan.Zero), result.StartUtc);
    }

    [Fact]
    public void Ms365Exception_MatchingAlone_IsShownAsThatOccurrence()
    {
        const string series = """ "seriesMasterId":"master", """;
        _store.ApplyServerItem(["g1"], Graph("o1", "Standup", "2026-11-07T01:00:00", """ "type":"occurrence", """ + series) with { RemoteId = "o1" });
        _store.ApplyServerItem(["g1"], Graph("x1", "Standup with demo", "2026-11-09T01:00:00", """ "type":"exception", """ + series) with { RemoteId = "x1" });

        Assert.Equal("x1", Assert.Single(_store.Search("demo")).ItemId);
    }

    [Fact]
    public void RadicaleSeries_IsOneResult_WithItsRecurrence()
    {
        _store.CreateItem(CalDav("r1", "Gym", "20261001T010000Z", "RRULE:FREQ=WEEKLY\n"));

        var result = Assert.Single(_store.Search("gym"));

        Assert.True(result.IsSeries);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 1, 0, 0, TimeSpan.Zero), result.StartUtc);
        Assert.Equal(result.StartUtc, result.RecurrenceUtc);
    }

    [Fact]
    public void UpcomingComeFirstSoonestFirst_ThenPastNewestFirst()
    {
        _store.CreateItem(CalDav("past-old", "Review", "20261001T010000Z"));
        _store.CreateItem(CalDav("soon", "Review", "20261107T010000Z"));
        _store.CreateItem(CalDav("past-new", "Review", "20261101T010000Z"));
        _store.CreateItem(CalDav("later", "Review", "20261201T010000Z"));

        Assert.Equal(["soon", "later", "past-new", "past-old"], _store.Search("review").Select(r => r.ItemId));
    }

    [Fact]
    public void KindFilter_ListsEventsOrTasksOnly()
    {
        _store.CreateItem(CalDav("r1", "Review notes", "20261110T010000Z"));
        _store.CreateItem(Todo("t1", "Review forms", "20261111T090000Z"));

        Assert.Equal(["r1"], _store.Search("review", new SearchFilter(Kind: SearchKind.Events)).Select(r => r.ItemId));
        Assert.Equal(["t1"], _store.Search("review", new SearchFilter(Kind: SearchKind.Tasks)).Select(r => r.ItemId));
        Assert.Equal(["r1", "t1"], _store.Search("review", new SearchFilter(Kind: SearchKind.All)).Select(r => r.ItemId).Order());
    }

    [Fact]
    public void CollectionFilter_ListsOnlyTheGivenCollections_AndHiddenStayExcluded()
    {
        _store.CreateItem(CalDav("r1", "Review notes", "20261110T010000Z"));
        _store.CreateItem(Graph("m1", "Review budget", "2026-11-11T01:00:00"));

        Assert.Equal(["m1"], _store.Search("review", new SearchFilter(CollectionIds: ["g1"])).Select(r => r.ItemId));
        Assert.Equal(["m1", "r1"], _store.Search("review", new SearchFilter(CollectionIds: ["c1", "g1"])).Select(r => r.ItemId).Order());
        Assert.Empty(_store.Search("review", new SearchFilter(CollectionIds: [])));

        _store.SetCollectionHidden("g1", true);
        Assert.Empty(_store.Search("review", new SearchFilter(CollectionIds: ["g1"])));
        Assert.Equal(["r1"], _store.Search("review", new SearchFilter(CollectionIds: ["c1", "g1"])).Select(r => r.ItemId));
    }

    [Fact]
    public void DateRangeFilter_ShowsTheOccurrenceInRange_AndDropsOutsideAndUndated()
    {
        _store.CreateItem(CalDav("r1", "Gym", "20261001T010000Z", "RRULE:FREQ=WEEKLY\n"));
        _store.CreateItem(CalDav("club", "Gym club", "20261016T020000Z"));
        _store.CreateItem(CalDav("later", "Gym later", "20261201T010000Z"));
        _store.CreateItem(Todo("undated", "Gym plan"));
        _store.CreateItem(Todo("due", "Gym pay", "20261016T090000Z"));
        var range = new SearchFilter(FromUtc: Utc(10, 14), ToUtc: Utc(10, 21));

        var results = _store.Search("gym", range);

        Assert.Equal(["club", "due", "r1"], results.Select(r => r.ItemId).Order());
        var series = results.Single(r => r.ItemId == "r1");
        Assert.True(series.IsSeries);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 1, 0, 0, TimeSpan.Zero), series.StartUtc);
        Assert.Equal(["later"], _store.Search("gym", new SearchFilter(FromUtc: Utc(11, 30))).Select(r => r.ItemId).Where(i => i == "later"));
    }

    [Fact]
    public void Limit_AppliesAfterTheFilter()
    {
        for (var i = 0; i < 55; i++)
        {
            _store.CreateItem(Graph($"m{i}", "Match", "2026-11-11T01:00:00"));
        }
        for (var i = 0; i < 5; i++)
        {
            _store.CreateItem(CalDav($"h{i}", "Match", "20261110T010000Z"));
        }

        Assert.Equal(60, _store.Search("match", limit: 100).Count);
        Assert.Equal(5, _store.Search("match", new SearchFilter(CollectionIds: ["c1"])).Count);
        Assert.Equal(50, _store.Search("match", new SearchFilter(FromUtc: Utc(11, 1), ToUtc: Utc(12, 1))).Count);
    }

    [Fact]
    public void FilterOnly_ListsEverythingInRange_SortedAsSearchSorts_WithPlainTitles()
    {
        _store.CreateItem(CalDav("far", "Alpha", "20261120T010000Z"));
        _store.CreateItem(CalDav("near", "Beta", "20261110T010000Z"));
        _store.CreateItem(CalDav("past", "Gamma", "20261102T010000Z"));
        _store.CreateItem(CalDav("out", "Delta", "20261220T010000Z"));
        _store.CreateItem(Todo("undated", "Epsilon"));

        var results = _store.Search("", new SearchFilter(FromUtc: Utc(11, 1), ToUtc: Utc(12, 1)));

        Assert.Equal(["near", "far", "past"], results.Select(r => r.ItemId));
        Assert.Equal(["Beta", "Alpha", "Gamma"], results.Select(r => r.Title));
        Assert.All(results, r => Assert.Null(r.Snippet));
        Assert.Empty(_store.Search(""));
        Assert.Empty(_store.Search("   ", new SearchFilter()));
    }

    [Fact]
    public void AttendeeFilter_MatchesThePeopleColumnOnly_AsAnExactEntry()
    {
        _store.CreateItem(CalDav("e1", "Meeting with Alex Smith", "20261110T010000Z"));
        _store.CreateItem(CalDav("e2", "Planning", "20261111T010000Z", "ATTENDEE;CN=Alex Smith:mailto:alex@example.test\n"));
        _store.CreateItem(CalDav("e3", "Other", "20261112T010000Z", "ATTENDEE;CN=Alexandra Smithers:mailto:alexandra@example.test\n"));

        Assert.Equal(["e2"], _store.Search("", new SearchFilter(Attendee: "Alex Smith")).Select(r => r.ItemId));
        Assert.Equal(["e2"], _store.Search("", new SearchFilter(Attendee: "alex@example.test")).Select(r => r.ItemId));
        Assert.Equal(["e2"], _store.Search("plan", new SearchFilter(Attendee: "Alex Smith")).Select(r => r.ItemId));
        Assert.Empty(_store.Search("meeting", new SearchFilter(Attendee: "Alex Smith")));
    }

    [Fact]
    public void LocationFilter_MatchesTheLocationColumnOnly()
    {
        _store.CreateItem(CalDav("l1", "Boardroom booking", "20261110T010000Z", "DESCRIPTION:Boardroom needed\n"));
        _store.CreateItem(CalDav("l2", "Sync", "20261111T010000Z", "LOCATION:Boardroom Level 2\n"));

        Assert.Equal(["l2"], _store.Search("", new SearchFilter(Location: "boardroom")).Select(r => r.ItemId));
        Assert.Equal(["l2"], _store.Search("", new SearchFilter(Location: "board lev")).Select(r => r.ItemId));
        Assert.Equal(["l2"], _store.Search("sync", new SearchFilter(Location: "boardroom")).Select(r => r.ItemId));
        Assert.Empty(_store.Search("booking", new SearchFilter(Location: "boardroom")));
    }

    [Fact]
    public void TitleWordsFilter_MatchesTheTitleColumnOnly()
    {
        _store.CreateItem(CalDav("tw1", "Monday standup", "20261110T010000Z"));
        _store.CreateItem(CalDav("tw2", "Planning", "20261111T010000Z", "DESCRIPTION:Monday agenda\n"));

        Assert.Equal(["tw1"], _store.Search("", new SearchFilter(TitleWords: "monday")).Select(r => r.ItemId));
        Assert.Equal(["tw1"], _store.Search("standup", new SearchFilter(TitleWords: "mon")).Select(r => r.ItemId));
        Assert.Empty(_store.Search("planning", new SearchFilter(TitleWords: "monday")));
    }

    [Fact]
    public void Search_Over10000Items_IsUnder100ms()
    {
        var start = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        using (var db = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            // Bulk rows straight into the tables, as a sync of a large calendar leaves them.
            db.Open();
            using var tx = db.BeginTransaction();
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO items (id, collection_id, kind, raw_payload, summary, start_utc, end_utc, all_day, location, is_recurring)
                VALUES ($id, 'c1', 'Event', '{}', $summary, $s, $e, 0, $location, 0);
                INSERT INTO occurrences (item_id, start_utc, end_utc) VALUES ($id, $s, $e);
                INSERT INTO search_index (rowid, title, body, location, calendar, people)
                VALUES (last_insert_rowid(), $summary, $body, $location, 'Home', $people);
                """;
            var id = cmd.Parameters.Add("$id", SqliteType.Text);
            var summary = cmd.Parameters.Add("$summary", SqliteType.Text);
            var s = cmd.Parameters.Add("$s", SqliteType.Integer);
            var e = cmd.Parameters.Add("$e", SqliteType.Integer);
            var location = cmd.Parameters.Add("$location", SqliteType.Text);
            var body = cmd.Parameters.Add("$body", SqliteType.Text);
            var people = cmd.Parameters.Add("$people", SqliteType.Text);
            for (var i = 0; i < 10_000; i++)
            {
                var at = start.AddHours(i);
                (id.Value, summary.Value, location.Value) = ($"i{i}", $"Meeting {i} project {i % 97}", $"Room {i % 40}");
                (s.Value, e.Value) = (at.ToUnixTimeMilliseconds(), at.AddHours(1).ToUnixTimeMilliseconds());
                people.Value = $"Alex Smith\nalex{i % 50}@example.test";
                body.Value = $"Notes for meeting {i} with agenda items and follow ups for team {i % 13}";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        _store.Search("agenda"); // Warm-up: JIT and the SQLite page cache.

        // "meeting" matches every item, the worst case. Best of five.
        foreach (var text in new[] { "meeting", "project 42", "room 7", "alex" })
        {
            var best = long.MaxValue;
            for (var run = 0; run < 5; run++)
            {
                var watch = Stopwatch.StartNew();
                Assert.NotEmpty(_store.Search(text));
                best = Math.Min(best, watch.ElapsedMilliseconds);
            }

            Assert.True(best < 100, $"\"{text}\" took {best} ms");
        }

        // Filtered: words and a date range, a date range alone (one month), and an attendee.
        var filtered = new (string Name, string Text, SearchFilter Filter)[]
        {
            ("words and range", "meeting", new SearchFilter(FromUtc: start.AddDays(100), ToUtc: start.AddDays(130))),
            ("range only", "", new SearchFilter(FromUtc: start.AddDays(100), ToUtc: start.AddDays(130))),
            ("attendee", "", new SearchFilter(Attendee: "alex7@example.test")),
            ("words and attendee", "meeting", new SearchFilter(Attendee: "Alex Smith")),
        };
        foreach (var (name, text, filter) in filtered)
        {
            var best = long.MaxValue;
            for (var run = 0; run < 5; run++)
            {
                var watch = Stopwatch.StartNew();
                Assert.NotEmpty(_store.Search(text, filter));
                best = Math.Min(best, watch.ElapsedMilliseconds);
            }

            Assert.True(best < 100, $"{name} took {best} ms");
        }
    }
}
