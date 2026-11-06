using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>Store side of pushing (sync-radicale R6), conflicts (conflict-resolution R1-R4) and moves (calendar-views R8).</summary>
public sealed class ConflictStoreTests : IDisposable
{
    private const string Href = "https://example.test/cal/i1.ics";
    private static readonly string[] Collections = ["c1", "c2"];

    private readonly string _dir = Directory.CreateTempSubdirectory("wincal-conflict-").FullName;
    private readonly LocalStore _store;

    public ConflictStoreTests()
    {
        _store = LocalStore.Open(Path.Combine(_dir, "store.db"));
        _store.AddAccount(new Account("a1", AccountType.CalDAV, "Radicale"));
        _store.AddCollection(new Collection("c1", "a1", CollectionKind.Calendar, "Home", "Navy", "Circle", true, false, "https://example.test/cal/"));
        _store.AddCollection(new Collection("c2", "a1", CollectionKind.Calendar, "Work", "Teal", "Square", true, false, "https://example.test/work/"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static Item NewItem(string payload = "local", string? etag = null, string id = "i1", string collection = "c1", string? href = Href) =>
        new(id, collection, ItemKind.Event, href, payload, Summary: payload,
            StartUtc: new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero),
            EndUtc: new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero), Etag: etag);

    private void SeedSynced(string etag = "\"e1\"")
    {
        _store.CreateItem(NewItem("v1"));
        _store.CompleteOutboxWrite(_store.GetOutbox()[0].Id, etag);
    }

    [Fact]
    public void CompleteWrite_RecordsEtagAndRebasesLaterEntries()
    {
        _store.CreateItem(NewItem("v1"));
        var create = _store.GetOutbox()[0];
        _store.MarkInFlight(create.Id);
        _store.UpdateItem(NewItem("v2"));

        _store.CompleteOutboxWrite(create.Id, "\"e2\"");

        var update = Assert.Single(_store.GetOutbox());
        Assert.Equal("\"e2\"", update.BaseEtag);
        Assert.Equal("\"e2\"", _store.GetItem("i1")!.Etag);
    }

    [Fact]
    public void ResolveConflict_ServerVersionWinsAndLocalIsKept()
    {
        SeedSynced();
        _store.UpdateItem(NewItem("mine", "\"e1\""));

        _store.ResolveConflict(_store.GetOutbox()[0].Id, NewItem("theirs", "\"e9\"", id: "x"));

        Assert.Empty(_store.GetOutbox());
        var item = _store.GetItem("i1")!;
        Assert.Equal(("theirs", "\"e9\"", "c1"), (item.RawPayload, item.Etag, item.CollectionId));
        Assert.Equal(1, _store.GetConflictCount());
    }

    [Fact]
    public void ResolveConflict_ServerGone_RemovesItem()
    {
        SeedSynced();
        _store.UpdateItem(NewItem("mine", "\"e1\""));

        _store.ResolveConflict(_store.GetOutbox()[0].Id, null);

        Assert.Null(_store.GetItem("i1", includeDeleted: true));
        Assert.Equal(1, _store.GetConflictCount());
    }

    [Fact]
    public void ServerEdit_OfLocallyDeletedItem_RestoresServerVersion()
    {
        SeedSynced();
        _store.DeleteItem("i1");

        var result = _store.ApplyServerItem(Collections, NewItem("theirs", "\"e9\"", id: "x"));

        Assert.Equal(ServerChangeResult.Conflict, result);
        Assert.Equal("theirs", _store.GetItem("i1")!.RawPayload);
        Assert.Empty(_store.GetOutbox());
        Assert.Equal(1, _store.GetConflictCount());
    }

    [Fact]
    public void ServerEdit_WhileSending_KeepsLocal()
    {
        SeedSynced();
        _store.UpdateItem(NewItem("mine", "\"e1\""));
        _store.MarkInFlight(_store.GetOutbox()[0].Id);

        Assert.Equal(ServerChangeResult.KeptLocal, _store.ApplyServerItem(Collections, NewItem("theirs", "\"e9\"", id: "x")));
        Assert.Equal("mine", _store.GetItem("i1")!.RawPayload);
    }

    [Fact]
    public void ServerDelete_OfLocallyEditedItem_KeepsEditInConflicts()
    {
        SeedSynced();
        _store.UpdateItem(NewItem("mine", "\"e1\""));

        Assert.Equal(ServerChangeResult.Conflict, _store.RemoveServerItem(Collections, Href));
        Assert.Null(_store.GetItem("i1", includeDeleted: true));
        Assert.Empty(_store.GetOutbox());
        Assert.Equal(1, _store.GetConflictCount());
    }

    [Fact]
    public void ServerDelete_OfLocallyDeletedItem_JustAgrees()
    {
        SeedSynced();
        _store.DeleteItem("i1");

        Assert.Equal(ServerChangeResult.Applied, _store.RemoveServerItem(Collections, Href));
        Assert.Empty(_store.GetOutbox());
        Assert.Equal(0, _store.GetConflictCount());
    }

    [Fact]
    public void FullResync_KeepsUnsentCreate()
    {
        _store.CreateItem(NewItem());

        Assert.Equal(ServerChangeResult.KeptLocal, _store.RemoveServerItem(Collections, Href));
        Assert.NotNull(_store.GetItem("i1"));
    }

    [Fact]
    public void MoveUnsentItem_RetargetsCreate()
    {
        _store.CreateItem(NewItem());

        var id = _store.MoveItem("i1", NewItem("moved", id: "new", collection: "c2", href: "https://example.test/work/i1.ics"));

        Assert.Equal("i1", id);
        Assert.Equal("c2", _store.GetItem("i1")!.CollectionId);
        var entry = Assert.Single(_store.GetOutbox());
        Assert.Equal((OutboxOperation.Create, "moved"), (entry.Operation, entry.Payload));
    }

    [Fact]
    public void MoveSyncedItem_QueuesCreateThenDeleteAsOneGroup()
    {
        SeedSynced();

        var id = _store.MoveItem("i1", NewItem("v1", id: "new", collection: "c2", href: "https://example.test/work/i1.ics"));

        Assert.Equal("new", id);
        Assert.Null(_store.GetItem("i1"));
        var entries = _store.GetOutbox();
        Assert.Equal([(OutboxOperation.Create, "new"), (OutboxOperation.Delete, "i1")], entries.Select(e => (e.Operation, e.ItemId)));
        Assert.Equal(entries[0].GroupId, entries[1].GroupId);
        Assert.Equal("\"e1\"", entries[1].BaseEtag);
        // The delete waits for the create.
        Assert.Equal("new", Assert.Single(_store.GetDueOutbox()).ItemId);
    }

    [Fact]
    public void SplitItem_QueuesUpdateThenCreateAsOneGroup()
    {
        SeedSynced();

        _store.SplitItem(NewItem("short", "\"e1\""), NewItem("rest", id: "new", href: "https://example.test/cal/new.ics"));

        var entries = _store.GetOutbox();
        Assert.Equal([("i1", OutboxOperation.Update), ("new", OutboxOperation.Create)], entries.Select(e => (e.ItemId, e.Operation)));
        Assert.Equal(entries[0].GroupId, entries[1].GroupId);
        Assert.Equal("i1", Assert.Single(_store.GetDueOutbox()).ItemId);
    }

    [Fact]
    public void ConflictOnTruncate_DoesNotCreateTheNewSeries()
    {
        SeedSynced();
        _store.SplitItem(NewItem("short", "\"e1\""), NewItem("rest", id: "new", href: "https://example.test/cal/new.ics"));

        _store.ResolveConflict(_store.GetOutbox()[0].Id, NewItem("theirs", "\"e9\""));

        Assert.Empty(_store.GetOutbox());
        Assert.Null(_store.GetItem("new", includeDeleted: true));
        Assert.Equal("theirs", _store.GetItem("i1")!.RawPayload);
        Assert.Equal(2, _store.GetConflictCount());
    }

    [Fact]
    public void ConflictOnMoveCopy_KeepsTheOriginal()
    {
        SeedSynced();
        _store.MoveItem("i1", NewItem("v1", id: "new", collection: "c2", href: "https://example.test/work/i1.ics"));

        _store.ResolveConflict(_store.GetOutbox()[0].Id, NewItem("other", "\"e5\"", collection: "c2"));

        Assert.Empty(_store.GetOutbox());
        Assert.Equal("v1", _store.GetItem("i1")!.RawPayload);
    }

    [Fact]
    public void UndoSplit_RestoresOriginalThenDeletesNewSeries()
    {
        SeedSynced();
        _store.SplitItem(NewItem("short", "\"e1\""), NewItem("rest", id: "new", href: "https://example.test/cal/new.ics"));

        _store.UndoSplit(NewItem("v1", "\"e1\""), "new");

        Assert.Equal("v1", _store.GetItem("i1")!.RawPayload);
        Assert.Null(_store.GetItem("new"));
        var undo = _store.GetOutbox().Skip(2).ToList();
        Assert.Equal([("i1", OutboxOperation.Update), ("new", OutboxOperation.Delete)], undo.Select(e => (e.ItemId, e.Operation)));
        Assert.Equal(undo[0].GroupId, undo[1].GroupId);
    }
}
