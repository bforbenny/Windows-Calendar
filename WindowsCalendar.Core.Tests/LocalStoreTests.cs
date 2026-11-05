using Microsoft.Data.Sqlite;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

public sealed class LocalStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wincal-store-").FullName;
    private string DbPath => Path.Combine(_dir, "store.db");
    private readonly LocalStore _store;

    public LocalStoreTests()
    {
        _store = LocalStore.Open(DbPath);
        _store.AddAccount(new Account("a1", AccountType.CalDAV, "Radicale"));
        _store.AddCollection(new Collection("c1", "a1", CollectionKind.Calendar, "Home", "Navy", "Circle", true, false, "https://example.test/cal/"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static Item NewItem(string id = "i1", string payload = "v1", string? etag = null) =>
        new(id, "c1", ItemKind.Event, null, payload, Summary: "Meeting",
            StartUtc: new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero),
            EndUtc: new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero), Etag: etag);

    [Fact]
    public void CreateItem_StoresItemAndQueuesCreate()
    {
        var item = NewItem();
        _store.CreateItem(item);

        Assert.Equal(item, _store.GetItem("i1"));
        var entry = Assert.Single(_store.GetOutbox());
        Assert.Equal(OutboxOperation.Create, entry.Operation);
        Assert.Equal("v1", entry.Payload);
    }

    [Fact]
    public void ChangesPersistAcrossReopen()
    {
        _store.CreateItem(NewItem());
        _store.Dispose();

        using var reopened = LocalStore.Open(DbPath);
        Assert.NotNull(reopened.GetItem("i1"));
        Assert.Single(reopened.GetOutbox());
    }

    [Fact]
    public void CreateThenUpdate_CoalescesToCreateWithNewPayload()
    {
        _store.CreateItem(NewItem());
        _store.UpdateItem(NewItem(payload: "v2"));

        var entry = Assert.Single(_store.GetOutbox());
        Assert.Equal(OutboxOperation.Create, entry.Operation);
        Assert.Equal("v2", entry.Payload);
    }

    [Fact]
    public void CreateThenDelete_LeavesNoEntryAndNoItem()
    {
        _store.CreateItem(NewItem());
        _store.DeleteItem("i1");

        Assert.Empty(_store.GetOutbox());
        Assert.Null(_store.GetItem("i1"));
    }

    [Fact]
    public void UpdateThenUpdate_KeepsFirstBaseEtag()
    {
        SeedSyncedItem(etag: "e1");
        _store.UpdateItem(NewItem(payload: "v2", etag: "e1"));
        _store.UpdateItem(NewItem(payload: "v3", etag: "e2"));

        var entry = Assert.Single(_store.GetOutbox());
        Assert.Equal(OutboxOperation.Update, entry.Operation);
        Assert.Equal("v3", entry.Payload);
        Assert.Equal("e1", entry.BaseEtag);
    }

    [Fact]
    public void UpdateThenDelete_BecomesDeleteAndKeepsTombstone()
    {
        SeedSyncedItem(etag: "e1");
        _store.UpdateItem(NewItem(payload: "v2", etag: "e1"));
        _store.DeleteItem("i1");

        var entry = Assert.Single(_store.GetOutbox());
        Assert.Equal(OutboxOperation.Delete, entry.Operation);
        Assert.Null(entry.Payload);
        Assert.Equal("e1", entry.BaseEtag);
        Assert.Null(_store.GetItem("i1"));
        Assert.Equal(1L, CountRows("SELECT COUNT(*) FROM items WHERE id = 'i1' AND deleted = 1"));
    }

    [Fact]
    public void CompletingDelete_RemovesTombstone()
    {
        SeedSyncedItem(etag: "e1");
        _store.DeleteItem("i1");
        _store.CompleteOutboxEntry(_store.GetOutbox()[0].Id);

        Assert.Empty(_store.GetOutbox());
        Assert.Equal(0L, CountRows("SELECT COUNT(*) FROM items"));
    }

    [Fact]
    public void InFlightEntry_IsNotCoalesced()
    {
        _store.CreateItem(NewItem());
        _store.MarkInFlight(_store.GetOutbox()[0].Id);
        _store.UpdateItem(NewItem(payload: "v2"));

        var entries = _store.GetOutbox();
        Assert.Equal(2, entries.Count);
        Assert.Equal("v1", entries[0].Payload);
        Assert.Equal(OutboxOperation.Update, entries[1].Operation);
    }

    [Fact]
    public void InFlightMarks_AreClearedOnReopen()
    {
        _store.CreateItem(NewItem());
        _store.MarkInFlight(_store.GetOutbox()[0].Id);
        _store.Dispose();

        using var reopened = LocalStore.Open(DbPath);
        Assert.False(reopened.GetOutbox()[0].InFlight);
    }

    [Fact]
    public void GroupedEntries_AreNotCoalescedAndKeepOrder()
    {
        _store.CreateItem(NewItem("i1"));
        _store.CreateItem(NewItem("i2"));
        _store.CompleteOutboxEntry(_store.GetOutbox()[0].Id);
        _store.CompleteOutboxEntry(_store.GetOutbox()[0].Id);

        _store.UpdateItem(NewItem("i1", payload: "v2"), groupId: "g1");
        _store.DeleteItem("i2", groupId: "g1");
        _store.UpdateItem(NewItem("i1", payload: "v3"));

        var entries = _store.GetOutbox();
        Assert.Equal(3, entries.Count);
        Assert.Equal(("i1", OutboxOperation.Update, "g1"), (entries[0].ItemId, entries[0].Operation, entries[0].GroupId));
        Assert.Equal(("i2", OutboxOperation.Delete, "g1"), (entries[1].ItemId, entries[1].Operation, entries[1].GroupId));
        Assert.Equal(("i1", OutboxOperation.Update, (string?)null), (entries[2].ItemId, entries[2].Operation, entries[2].GroupId));
    }

    [Fact]
    public void CoalescedEntry_KeepsQueuePosition()
    {
        _store.CreateItem(NewItem("i1"));
        _store.CreateItem(NewItem("i2"));
        _store.UpdateItem(NewItem("i1", payload: "v2"));

        Assert.Equal(["i1", "i2"], _store.GetOutbox().Select(e => e.ItemId));
    }

    [Fact]
    public void RecordFailure_IncrementsAttemptsAndClearsInFlight()
    {
        _store.CreateItem(NewItem());
        var id = _store.GetOutbox()[0].Id;
        _store.MarkInFlight(id);
        _store.RecordOutboxFailure(id, "503 Service Unavailable");

        var entry = _store.GetOutbox()[0];
        Assert.Equal(1, entry.Attempts);
        Assert.Equal("503 Service Unavailable", entry.LastError);
        Assert.False(entry.InFlight);
    }

    [Fact]
    public void UpdatingDeletedItem_Throws()
    {
        SeedSyncedItem(etag: "e1");
        _store.DeleteItem("i1");

        Assert.Throws<KeyNotFoundException>(() => _store.UpdateItem(NewItem()));
    }

    [Fact]
    public void SyncState_UpsertsPerCollectionAndKey()
    {
        Assert.Null(_store.GetSyncState("c1", "sync-token"));
        _store.SetSyncState("c1", "sync-token", "t1");
        _store.SetSyncState("c1", "sync-token", "t2");

        Assert.Equal("t2", _store.GetSyncState("c1", "sync-token"));
    }

    [Fact]
    public void NewerSchema_IsNotOpenedAndNotChanged()
    {
        _store.Dispose();
        var newer = Migrations.LatestVersion + 1;
        SetUserVersion(newer);

        var ex = Assert.Throws<StoreTooNewException>(() => LocalStore.Open(DbPath));
        Assert.Equal(newer, ex.StoreVersion);
        Assert.Equal(newer, GetUserVersion());
    }

    /// <summary>An item that is already on the server: created, then its create entry completed.</summary>
    private void SeedSyncedItem(string etag)
    {
        _store.CreateItem(NewItem(etag: etag));
        _store.CompleteOutboxEntry(_store.GetOutbox()[0].Id);
    }

    private SqliteConnection OpenRaw()
    {
        var c = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        c.Open();
        return c;
    }

    private long CountRows(string sql)
    {
        using var c = OpenRaw();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private void SetUserVersion(int version)
    {
        using var c = OpenRaw();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    private long GetUserVersion() => CountRows("PRAGMA user_version;");
}
