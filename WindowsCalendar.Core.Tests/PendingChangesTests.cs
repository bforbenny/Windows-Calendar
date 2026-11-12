using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>Pending changes list and Discard (local-store-offline R7, R9a; conflict-resolution R9), first-sync mark (ui-states R1a).</summary>
public sealed class PendingChangesTests : IDisposable
{
    private const string Href = "https://example.test/cal/i1.ics";

    private readonly string _dir = Directory.CreateTempSubdirectory("wincal-pending-").FullName;
    private readonly LocalStore _store;

    public PendingChangesTests()
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

    private static Item NewItem(string payload, string? etag = null, string? href = Href) =>
        new("i1", "c1", ItemKind.Event, href, payload, Summary: payload,
            StartUtc: new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero),
            EndUtc: new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero), Etag: etag);

    private void SeedSynced()
    {
        _store.CreateItem(NewItem("v1"));
        _store.CompleteOutboxWrite(_store.GetOutbox()[0].Id, "\"e1\"");
        _store.SetSyncState("c1", "token", "t1");
        _store.SetSyncState("c2", "token", "t2");
    }

    [Fact]
    public void GetPendingChanges_ShowsTitleOperationAndFailure()
    {
        SeedSynced();
        _store.UpdateItem(NewItem("Dentist", "\"e1\""));
        var entry = _store.GetOutbox()[0];
        _store.RecordOutboxFailure(entry.Id, "HTTP 403", permanent: true);

        var change = Assert.Single(_store.GetPendingChanges());
        Assert.Equal(("Dentist", OutboxOperation.Update, "HTTP 403", true, true), (change.Title, change.Operation, change.LastError, change.Permanent, change.Failed));
    }

    [Fact]
    public void Discard_Update_KeepsLocalInConflictsAndRefetches()
    {
        SeedSynced();
        _store.UpdateItem(NewItem("mine", "\"e1\""));

        Assert.True(_store.DiscardOutboxEntry(_store.GetOutbox()[0].Id));

        Assert.Empty(_store.GetOutbox());
        Assert.Equal(1, _store.GetConflictCount());
        var item = _store.GetItem("i1")!;
        Assert.Null(item.Etag);
        // Only the item's own collection is read in full again.
        Assert.Null(_store.GetSyncState("c1", "token"));
        Assert.Equal("t2", _store.GetSyncState("c2", "token"));
    }

    [Fact]
    public void Discard_Delete_BringsItemBack()
    {
        SeedSynced();
        _store.DeleteItem("i1");

        Assert.True(_store.DiscardOutboxEntry(_store.GetOutbox()[0].Id));

        Assert.NotNull(_store.GetItem("i1"));
        Assert.Empty(_store.GetOutbox());
        Assert.True(Assert.Single(_store.GetConflicts()).LocalDeleted);
    }

    [Fact]
    public void Discard_UnsentCreate_RemovesItem()
    {
        _store.CreateItem(NewItem("new", href: null));

        Assert.True(_store.DiscardOutboxEntry(_store.GetOutbox()[0].Id));

        Assert.Null(_store.GetItem("i1", includeDeleted: true));
        Assert.Empty(_store.GetOutbox());
        Assert.Equal(1, _store.GetConflictCount());
    }

    [Fact]
    public void Discard_InFlight_IsRefused()
    {
        SeedSynced();
        _store.UpdateItem(NewItem("mine", "\"e1\""));
        var entry = _store.GetOutbox()[0];
        _store.MarkInFlight(entry.Id);

        Assert.False(_store.DiscardOutboxEntry(entry.Id));
        Assert.Single(_store.GetOutbox());
        Assert.Equal(0, _store.GetConflictCount());
    }

    [Fact]
    public void SyncedOnce_IsPerAccount()
    {
        Assert.False(_store.HasSyncedOnce("a1"));
        _store.MarkSyncedOnce("a1");
        Assert.True(_store.HasSyncedOnce("a1"));
        Assert.False(_store.HasSyncedOnce("a2"));
    }

    [Fact]
    public void SyncedOnce_AccountWithItemsCounts()
    {
        // Accounts added before the mark existed.
        SeedSynced();
        Assert.True(_store.HasSyncedOnce("a1"));
    }
}
