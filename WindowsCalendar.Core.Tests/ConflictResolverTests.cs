using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Sync;

namespace WindowsCalendar.Core.Tests;

/// <summary>Resolving a kept conflict (conflict-resolution R6, second slice).</summary>
public sealed class ConflictResolverTests : IDisposable
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    private readonly string _dir = Directory.CreateTempSubdirectory("wincal-resolver-").FullName;
    private readonly LocalStore _store;

    public ConflictResolverTests()
    {
        _store = LocalStore.Open(Path.Combine(_dir, "store.db"));
        _store.AddAccount(new Account("a1", AccountType.CalDAV, "Radicale"));
        _store.AddCollection(new Collection("c1", "a1", CollectionKind.Calendar, "Home", "Navy", "Circle", true, false, "https://example.test/cal/"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private const string ServerJson = """
        {"id":"g1","@odata.etag":"W/\"s2\"","subject":"Server title","start":{"dateTime":"2026-11-12T10:00:00","timeZone":"UTC"},
        "end":{"dateTime":"2026-11-12T11:00:00","timeZone":"UTC"},"isAllDay":false,"location":{"displayName":"Server room"},
        "showAs":"busy","isReminderOn":true,"reminderMinutesBeforeStart":15,"importance":"high","categories":["Blue"]}
        """;

    private static Item GraphServer() => GraphEventMapper.Map(JsonDocument.Parse(ServerJson).RootElement, "c1", Zone)!;

    /// <summary>The local version: a new title and start, and a location that is not listed as changed.</summary>
    private static string GraphLocal()
    {
        var ev = JsonNode.Parse(ServerJson)!.AsObject();
        ev["subject"] = "My title";
        ev["start"] = new JsonObject { ["dateTime"] = "2026-11-13T17:30:00", ["timeZone"] = "UTC" };
        ev["location"] = new JsonObject { ["displayName"] = "My room" };
        ev["importance"] = "low";
        ev[GraphEvents.ChangedKey] = new JsonArray("subject", "start");
        return ev.ToJsonString();
    }

    private static string Ics(string summary, string start, string end, string? location = null, string? alarm = null) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20261101T000000Z\r\n"
        + $"SUMMARY:{summary}\r\nDTSTART:{start}\r\nDTEND:{end}\r\n"
        + (location is null ? "" : $"LOCATION:{location}\r\n")
        + (alarm is null ? "" : $"BEGIN:VALARM\r\nACTION:DISPLAY\r\nDESCRIPTION:r\r\nTRIGGER:{alarm}\r\nEND:VALARM\r\n")
        + "END:VEVENT\r\nEND:VCALENDAR\r\n";

    private static Item CalDavServer()
    {
        var item = WindowsCalendar.Core.CalDav.CalDavItemMapper.Map("href1", "\"e9\"",
            Ics("Server title", "20261112T100000Z", "20261112T110000Z", "Server room", "-PT15M"), Zone)!;
        return item with { Id = "i1", CollectionId = "c1" };
    }

    [Fact]
    public void GraphDifferences_UseOnlyChangedFields()
    {
        var diffs = ConflictResolver.Differences(GraphLocal(), GraphServer(), graph: true, Zone);

        Assert.Equal(["Title", "Start"], diffs.Select(d => d.Field));
        Assert.Equal(("My title", "Server title"), (diffs[0].Yours, diffs[0].Server));
        Assert.Equal(("Fri 13 Nov 17:30", "10:00"), (diffs[1].Yours, diffs[1].Server));
    }

    [Fact]
    public void GraphUseMine_KeepsServerOnlyFields_AndPatchesOnlyChangedFields()
    {
        var server = GraphServer();

        var mine = ConflictResolver.UseMine(server, GraphLocal(), graph: true, Zone);

        var ev = JsonNode.Parse(mine.RawPayload)!.AsObject();
        Assert.Equal("My title", (string?)ev["subject"]);
        Assert.Equal("Server room", (string?)ev["location"]!["displayName"]);
        Assert.Equal("high", (string?)ev["importance"]);
        Assert.Equal("Blue", (string?)ev["categories"]![0]);
        Assert.Equal(("My title", server.Id, server.CollectionId, server.Etag), (mine.Summary, mine.Id, mine.CollectionId, mine.Etag));
        var patch = JsonNode.Parse(GraphEvents.PatchBody(mine.RawPayload))!.AsObject();
        Assert.Equal(["start", "subject"], patch.Select(p => p.Key).Order());
    }

    [Fact]
    public void CalDavDifferences_CompareAllFields()
    {
        var local = Ics("My title", "20261112T100000Z", "20261112T113000Z", alarm: "-PT30M");

        var diffs = ConflictResolver.Differences(local, CalDavServer(), graph: false, Zone);

        Assert.Equal(["Title", "End", "Location", "Reminder"], diffs.Select(d => d.Field));
        Assert.Equal(("11:30", "11:00"), (diffs[1].Yours, diffs[1].Server));
        Assert.Equal(("(none)", "Server room"), (diffs[2].Yours, diffs[2].Server));
        Assert.Equal(("30 min before", "15 min before"), (diffs[3].Yours, diffs[3].Server));
    }

    [Fact]
    public void CalDavUseMine_KeepsCurrentEtagAndIds()
    {
        var server = CalDavServer();
        var local = Ics("My title", "20261112T100000Z", "20261112T113000Z");

        var mine = ConflictResolver.UseMine(server, local, graph: false, Zone);

        Assert.Equal(local, mine.RawPayload);
        Assert.Equal(("\"e9\"", "i1", "c1", "href1", "My title"), (mine.Etag, mine.Id, mine.CollectionId, mine.RemoteId, mine.Summary));
    }

    [Fact]
    public void LocalDelete_HasNoDifferences()
    {
        Assert.Empty(ConflictResolver.Differences(null, GraphServer(), graph: true, Zone));
        Assert.Empty(ConflictResolver.Differences(null, CalDavServer(), graph: false, Zone));
    }

    [Fact]
    public void Store_GetConflict_AndDeleteConflict()
    {
        var item = new Item("i1", "c1", ItemKind.Event, "https://example.test/cal/i1.ics", "v1", Summary: "v1",
            StartUtc: new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero), EndUtc: new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
        _store.CreateItem(item);
        _store.CompleteOutboxWrite(_store.GetOutbox()[0].Id, "\"e1\"");
        _store.UpdateItem(item with { RawPayload = "mine", Etag = "\"e1\"" });
        _store.ResolveConflict(_store.GetOutbox()[0].Id, item with { RawPayload = "theirs", Etag = "\"e9\"" });
        var id = _store.GetConflicts()[0].Id;

        var conflict = _store.GetConflict(id);

        Assert.Equal(new ConflictRecord(id, "i1", "c1", ItemKind.Event, "mine"), conflict);
        Assert.Null(_store.GetConflict(id + 100));

        _store.DeleteConflict(id);

        Assert.Equal(0, _store.GetConflictCount());
        Assert.Null(_store.GetConflict(id));
    }
}
