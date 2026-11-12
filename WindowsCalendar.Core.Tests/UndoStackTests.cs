using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>The undo stack and its store side (undo R2, R4-R6, R8).</summary>
public sealed class UndoStackTests : IDisposable
{
    private const string Href = "https://example.test/cal/i1.ics";
    private static readonly string[] Collections = ["c1", "c2"];

    private readonly string _dir = Directory.CreateTempSubdirectory("wincal-undo-").FullName;
    private readonly LocalStore _store;

    public UndoStackTests()
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

    private static Item NewItem(string payload = "v1", string? etag = null, string id = "i1", string collection = "c1", string? href = Href) =>
        new(id, collection, ItemKind.Event, href, payload, Summary: payload,
            StartUtc: new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero),
            EndUtc: new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero), Etag: etag);

    // As the app's mapper restore, without parsing: the earlier payload over the current etag; re-created as it was.
    private static Item? Restore(Item before, Item? current) => current is null ? before with { Etag = null } : before with { Etag = current.Etag };

    private UndoResult Undo() => _store.UndoLast(Restore);

    /// <summary>i1 at v1, sent, with etag e1; nothing queued and nothing on the stack.</summary>
    private void SeedSynced()
    {
        _store.CreateItem(NewItem("v1"));
        _store.CompleteOutboxWrite(_store.GetOutbox()[0].Id, "\"e1\"");
    }

    private void Update(string payload)
    {
        using var undo = _store.BeginUndo("Event edited");
        _store.UpdateItem(NewItem(payload, "\"e1\""));
    }

    private void Delete(string id = "i1")
    {
        using var undo = _store.BeginUndo("Event deleted");
        _store.DeleteItem(id);
    }

    [Fact]
    public void WritesOutsideAScope_AreNotRecorded()
    {
        SeedSynced();
        _store.UpdateItem(NewItem("v2", "\"e1\""));

        Assert.Equal(0, _store.UndoSteps.Count);
        Assert.Equal(UndoOutcome.Nothing, Undo().Outcome);
    }

    [Fact]
    public void Stack_KeepsTheLatestTwentySteps()
    {
        for (var i = 0; i < 21; i++)
        {
            using var undo = _store.BeginUndo("Event added");
            _store.CreateItem(NewItem($"v{i}", id: $"i{i}"));
        }

        Assert.Equal(UndoStack.Limit, _store.UndoSteps.Count);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(UndoOutcome.Done, Undo().Outcome);
        }
        Assert.Equal(UndoOutcome.Nothing, Undo().Outcome);
        // The oldest step fell off the stack, so its item stays.
        Assert.NotNull(_store.GetItem("i0"));
        Assert.Null(_store.GetItem("i1"));
    }

    [Fact]
    public void Steps_UndoNewestFirst()
    {
        SeedSynced();
        Update("v2");
        Update("v3");

        Undo();
        Assert.Equal("v2", _store.GetItem("i1")!.RawPayload);
        Undo();
        Assert.Equal("v1", _store.GetItem("i1")!.RawPayload);
    }

    [Fact]
    public void ScopeWithSeveralWrites_IsOneStep()
    {
        SeedSynced();
        using (_store.BeginUndo("Event edited"))
        {
            _store.UpdateItem(NewItem("v2", "\"e1\""));
            using (_store.BeginUndo("inner"))
            {
                _store.UpdateItem(NewItem("v3", "\"e1\""));
            }
        }

        var step = Assert.Single(_store.UndoSteps.All);
        var change = Assert.Single(step.Changes);
        Assert.Equal(("v1", "v3"), (change.Before!.RawPayload, change.After!.RawPayload));
    }

    [Fact]
    public void UndoEdit_RestoredVersionCanBeUndoneFurther()
    {
        // The mapper may write the payload back differently; the older step then expects what was written.
        SeedSynced();
        Update("v2");
        Update("v3");
        var restore = (Item before, Item? current) => before with { RawPayload = before.RawPayload + "'", Etag = current?.Etag };

        Assert.Equal(UndoOutcome.Done, _store.UndoLast(restore).Outcome);
        Assert.Equal(UndoOutcome.Done, _store.UndoLast(restore).Outcome);
        Assert.Equal("v1'", _store.GetItem("i1")!.RawPayload);
    }

    [Fact]
    public void UndoUnsentEdit_CoalescesWithIt()
    {
        SeedSynced();
        Update("v2");

        Undo();

        Assert.Equal("v1", _store.GetItem("i1")!.RawPayload);
        var entry = Assert.Single(_store.GetOutbox());
        Assert.Equal((OutboxOperation.Update, "v1"), (entry.Operation, entry.Payload));
    }

    [Fact]
    public void UndoEdit_RefusedWhenTheServerChangedTheItem()
    {
        SeedSynced();
        Update("v2");
        _store.CompleteOutboxWrite(_store.GetOutbox()[0].Id, "\"e2\"");
        _store.ApplyServerItem(Collections, NewItem("theirs", "\"e3\"", id: "s1"));

        var result = Undo();

        Assert.Equal(UndoOutcome.ChangedOnServer, result.Outcome);
        Assert.Equal("theirs", _store.GetItem("i1")!.RawPayload);
        Assert.Empty(_store.GetOutbox());
        Assert.Equal(0, _store.UndoSteps.Count);
    }

    [Fact]
    public void UndoUnsentDelete_CancelsIt()
    {
        SeedSynced();
        Delete();

        Assert.Equal(UndoOutcome.Done, Undo().Outcome);

        Assert.Equal("v1", _store.GetItem("i1")!.RawPayload);
        Assert.Equal("\"e1\"", _store.GetItem("i1")!.Etag);
        Assert.Empty(_store.GetOutbox());
        Assert.NotEmpty(_store.GetOccurrences(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
    }

    [Fact]
    public void UndoUnsentDelete_PutsBackTheUpdateItTookOver()
    {
        SeedSynced();
        _store.UpdateItem(NewItem("v2", "\"e1\""));
        Delete();
        Assert.Equal(OutboxOperation.Delete, Assert.Single(_store.GetOutbox()).Operation);

        Undo();

        Assert.Equal("v2", _store.GetItem("i1")!.RawPayload);
        var entry = Assert.Single(_store.GetOutbox());
        Assert.Equal((OutboxOperation.Update, "v2", "\"e1\""), (entry.Operation, entry.Payload, entry.BaseEtag));
    }

    [Fact]
    public void UndoDelete_OfANeverSentItem_CreatesItAgain()
    {
        _store.CreateItem(NewItem("v1"));
        Delete();
        Assert.Null(_store.GetItem("i1", includeDeleted: true));

        Undo();

        Assert.Equal("v1", _store.GetItem("i1")!.RawPayload);
        Assert.Equal(OutboxOperation.Create, Assert.Single(_store.GetOutbox()).Operation);
    }

    [Fact]
    public void UndoSentDelete_RecreatesFromTheStoredPayload()
    {
        SeedSynced();
        Delete();
        _store.CompleteOutboxEntry(_store.GetOutbox()[0].Id);
        Assert.Null(_store.GetItem("i1", includeDeleted: true));

        Assert.Equal(UndoOutcome.Done, Undo().Outcome);

        var item = _store.GetItem("i1")!;
        Assert.Equal(("v1", Href, null), (item.RawPayload, item.RemoteId, item.Etag));
        var entry = Assert.Single(_store.GetOutbox());
        Assert.Equal((OutboxOperation.Create, "v1"), (entry.Operation, entry.Payload));
    }

    [Fact]
    public void UndoSentDelete_WithANewId_IsReportedAsRecreated()
    {
        SeedSynced();
        Delete();
        _store.CompleteOutboxEntry(_store.GetOutbox()[0].Id);

        var result = _store.UndoLast((before, _) => before with { Id = "fresh", RemoteId = null, Etag = null });

        Assert.Equal(UndoOutcome.Recreated, result.Outcome);
        Assert.Equal("v1", _store.GetItem("fresh")!.RawPayload);
    }

    [Fact]
    public void UndoSentDelete_RefusedWhenItCannotBeRecreated()
    {
        SeedSynced();
        Delete();
        _store.CompleteOutboxEntry(_store.GetOutbox()[0].Id);

        Assert.Equal(UndoOutcome.CannotRecreate, _store.UndoLast((_, _) => null).Outcome);
        Assert.Empty(_store.GetOutbox());
        Assert.Equal(0, _store.UndoSteps.Count);
    }

    [Fact]
    public void UndoDelete_WhileBeingSent_WaitsAndKeepsTheStep()
    {
        SeedSynced();
        Delete();
        _store.MarkInFlight(_store.GetOutbox()[0].Id);

        Assert.Equal(UndoOutcome.Sending, Undo().Outcome);
        Assert.Equal(1, _store.UndoSteps.Count);
        Assert.Null(_store.GetItem("i1"));
    }

    [Fact]
    public void UndoUnsentCreate_RemovesItOutright()
    {
        using (_store.BeginUndo("Event added"))
        {
            _store.CreateItem(NewItem("v1"));
        }

        Undo();

        Assert.Null(_store.GetItem("i1", includeDeleted: true));
        Assert.Empty(_store.GetOutbox());
    }

    [Fact]
    public void UndoSentCreate_QueuesADelete()
    {
        using (_store.BeginUndo("Event added"))
        {
            _store.CreateItem(NewItem("v1"));
        }
        _store.CompleteOutboxWrite(_store.GetOutbox()[0].Id, "\"e1\"");

        Undo();

        Assert.Null(_store.GetItem("i1"));
        var entry = Assert.Single(_store.GetOutbox());
        Assert.Equal((OutboxOperation.Delete, "\"e1\""), (entry.Operation, entry.BaseEtag));
    }

    [Fact]
    public void UndoUnsentMove_CancelsBoth()
    {
        SeedSynced();
        using (_store.BeginUndo("Event moved"))
        {
            _store.MoveItem("i1", NewItem("v1", id: "new", collection: "c2", href: "https://example.test/work/i1.ics"));
        }

        Undo();

        Assert.Equal("c1", _store.GetItem("i1")!.CollectionId);
        Assert.Null(_store.GetItem("new", includeDeleted: true));
        Assert.Empty(_store.GetOutbox());
    }

    [Fact]
    public void UndoSentMove_RecreatesTheOriginalThenDeletesTheCopy_AsOneGroup()
    {
        SeedSynced();
        using (_store.BeginUndo("Event moved"))
        {
            _store.MoveItem("i1", NewItem("v1", id: "new", collection: "c2", href: "https://example.test/work/i1.ics"));
        }
        var outbox = _store.GetOutbox();
        _store.CompleteOutboxWrite(outbox[0].Id, "\"e2\"");
        _store.CompleteOutboxEntry(outbox[1].Id);

        Undo();

        Assert.Equal("c1", _store.GetItem("i1")!.CollectionId);
        Assert.Null(_store.GetItem("new"));
        var undo = _store.GetOutbox();
        Assert.Equal([("i1", OutboxOperation.Create), ("new", OutboxOperation.Delete)], undo.Select(e => (e.ItemId, e.Operation)));
        Assert.NotNull(undo[0].GroupId);
        Assert.Equal(undo[0].GroupId, undo[1].GroupId);
    }

    [Fact]
    public void UndoRetargetedCreate_MovesItBack()
    {
        _store.CreateItem(NewItem("v1"));
        using (_store.BeginUndo("Event moved"))
        {
            _store.MoveItem("i1", NewItem("w1", id: "new", collection: "c2"));
        }

        Undo();

        Assert.Equal(("c1", "v1"), (_store.GetItem("i1")!.CollectionId, _store.GetItem("i1")!.RawPayload));
        var entry = Assert.Single(_store.GetOutbox());
        Assert.Equal((OutboxOperation.Create, "v1"), (entry.Operation, entry.Payload));
    }

    [Fact]
    public void UndoSentSplit_RestoresTheOriginalThenDeletesTheNewSeries_AsOneGroup()
    {
        SeedSynced();
        using (_store.BeginUndo("Following events moved"))
        {
            _store.SplitItem(NewItem("short", "\"e1\""), NewItem("rest", id: "new", href: "https://example.test/cal/new.ics"));
        }
        var outbox = _store.GetOutbox();
        _store.CompleteOutboxWrite(outbox[0].Id, "\"e2\"");
        _store.CompleteOutboxWrite(outbox[1].Id, "\"n1\"");

        Undo();

        Assert.Equal("v1", _store.GetItem("i1")!.RawPayload);
        Assert.Null(_store.GetItem("new"));
        var undo = _store.GetOutbox();
        Assert.Equal([("i1", OutboxOperation.Update), ("new", OutboxOperation.Delete)], undo.Select(e => (e.ItemId, e.Operation)));
        Assert.Equal(undo[0].GroupId, undo[1].GroupId);
        Assert.Equal("\"e2\"", undo[0].BaseEtag);
    }

    [Fact]
    public void UndoUnsentSplit_DropsTheNewSeries()
    {
        SeedSynced();
        using (_store.BeginUndo("Following events moved"))
        {
            _store.SplitItem(NewItem("short", "\"e1\""), NewItem("rest", id: "new", href: "https://example.test/cal/new.ics"));
        }

        Undo();

        Assert.Equal("v1", _store.GetItem("i1")!.RawPayload);
        Assert.Null(_store.GetItem("new", includeDeleted: true));
        Assert.All(_store.GetOutbox(), e => Assert.Equal("i1", e.ItemId));
    }

    [Fact]
    public void UndoSplit_RefusedWhenTheNewSeriesChanged_AndNothingIsWritten()
    {
        SeedSynced();
        using (_store.BeginUndo("Following events moved"))
        {
            _store.SplitItem(NewItem("short", "\"e1\""), NewItem("rest", id: "new", href: "https://example.test/cal/new.ics"));
        }
        var outbox = _store.GetOutbox();
        _store.CompleteOutboxWrite(outbox[0].Id, "\"e2\"");
        _store.CompleteOutboxWrite(outbox[1].Id, "\"n1\"");
        // The original was changed on the server after the split.
        _store.ApplyServerItem(Collections, NewItem("theirs", "\"e3\"", id: "s"));

        Assert.Equal(UndoOutcome.ChangedOnServer, Undo().Outcome);
        Assert.Equal("theirs", _store.GetItem("i1")!.RawPayload);
        Assert.NotNull(_store.GetItem("new"));
        Assert.Empty(_store.GetOutbox());
    }

    [Fact]
    public void RefusedStep_ChangesNothing()
    {
        SeedSynced();
        using (var undo = _store.BeginUndo("Series moved"))
        {
            _store.UpdateItem(NewItem("v2", "\"e1\""));
            undo.Refuse("Can't put it back.");
        }

        var result = Undo();

        Assert.Equal(UndoOutcome.Refused, result.Outcome);
        Assert.Equal("Can't put it back.", result.Step!.Refusal);
        Assert.Equal("v2", _store.GetItem("i1")!.RawPayload);
    }

    [Fact]
    public void Restore_GraphOccurrenceDeletedOnTheServer_CannotBeRecreated()
    {
        var list = new Collection("g1", "a2", CollectionKind.Calendar, "Calendar", "Navy", "Circle", true, false, "");
        var occurrence = new Item("o1", "g1", ItemKind.Event, "AAMk-occ", "{}", SeriesId: "AAMk-master");

        Assert.Null(UndoRestore.For(occurrence, null, list, graph: true, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Restore_CalDavDeletedOnTheServer_KeepsIdAndHref()
    {
        var list = new Collection("c1", "a1", CollectionKind.Calendar, "Home", "Navy", "Circle", true, false, "https://example.test/cal/");

        var recreated = UndoRestore.For(NewItem("v1", "\"e1\""), null, list, graph: false, TimeZoneInfo.Utc)!;

        Assert.Equal(("i1", Href, null), (recreated.Id, recreated.RemoteId, recreated.Etag));
    }
}
