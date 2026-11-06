using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>Offline full-text search (search R1-R3, R6-R8).</summary>
public sealed class SearchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wincal-search-").FullName;
    private string DbPath => Path.Combine(_dir, "store.db");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly LocalStore _store;

    public SearchTests()
    {
        _store = LocalStore.Open(DbPath, _time);
        _store.AddAccount(new Account("a1", AccountType.CalDAV, "Radicale"));
        _store.AddCollection(new Collection("c1", "a1", CollectionKind.Calendar, "Home", "Navy", "Circle", true, false, "https://dav.example.test/home/"));
        _store.AddAccount(new Account("a2", AccountType.MS365, "Work"));
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
                INSERT INTO search_index (rowid, title, body, location, calendar)
                VALUES (last_insert_rowid(), $summary, $body, $location, 'Home');
                """;
            var id = cmd.Parameters.Add("$id", SqliteType.Text);
            var summary = cmd.Parameters.Add("$summary", SqliteType.Text);
            var s = cmd.Parameters.Add("$s", SqliteType.Integer);
            var e = cmd.Parameters.Add("$e", SqliteType.Integer);
            var location = cmd.Parameters.Add("$location", SqliteType.Text);
            var body = cmd.Parameters.Add("$body", SqliteType.Text);
            for (var i = 0; i < 10_000; i++)
            {
                var at = start.AddHours(i);
                (id.Value, summary.Value, location.Value) = ($"i{i}", $"Meeting {i} project {i % 97}", $"Room {i % 40}");
                (s.Value, e.Value) = (at.ToUnixTimeMilliseconds(), at.AddHours(1).ToUnixTimeMilliseconds());
                body.Value = $"Notes for meeting {i} with agenda items and follow ups for team {i % 13}";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        _store.Search("agenda"); // Warm-up: JIT and the SQLite page cache.

        // "meeting" matches every item, the worst case. Best of five, as other tests share the machine.
        foreach (var text in new[] { "meeting", "project 42", "room 7" })
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
    }
}
