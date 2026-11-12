using Microsoft.Data.Sqlite;

namespace WindowsCalendar.Core.Store;

/// <summary>
/// Undo (undo R2, R4-R6): the local writes (create, update, delete, move, split) made inside an <see cref="UndoScope"/>
/// are recorded as one step on <see cref="UndoSteps"/>; server-applied changes are never recorded. Undo reverts a whole
/// step in one transaction, in one linked outbox group when it touches more than one item.
/// </summary>
public sealed partial class LocalStore
{
    private UndoStep? _recording;
    private string? _scopeLabel;
    private int _scopeDepth;

    /// <summary>This session's undo steps (undo R2).</summary>
    public UndoStack UndoSteps { get; } = new();

    /// <summary>Starts (or joins) the undo step of one user action; the step goes on the stack at its first write.</summary>
    public UndoScope BeginUndo(string label)
    {
        if (_scopeDepth++ == 0)
        {
            _scopeLabel = label;
        }
        return new UndoScope(this);
    }

    internal void EndUndo()
    {
        if (--_scopeDepth == 0)
        {
            _recording = null;
            _scopeLabel = null;
        }
    }

    internal void RefuseUndo(string message)
    {
        if (_recording is { } step)
        {
            step.Refusal = message;
        }
    }

    /// <summary>Records one write of the open scope, if any; called inside the write's lock.</summary>
    private void Record(string itemId, Item? before, Item? after, bool deleteMergedUpdate = false)
    {
        if (_scopeDepth == 0)
        {
            return;
        }
        if (_recording is null)
        {
            _recording = new UndoStep(_scopeLabel!);
            UndoSteps.Push(_recording);
        }
        _recording.Add(new UndoChange(itemId, before, after, deleteMergedUpdate));
    }

    /// <summary>The stored row (also a tombstone) and whether it is deleted; null if there is none.</summary>
    private (Item Item, bool Deleted)? ReadRow(SqliteTransaction tx, string itemId)
    {
        using var cmd = Command(_connection, tx, $"SELECT {ItemColumns}, i.deleted FROM items i WHERE i.id = $id;", ("$id", itemId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (ReadItem(r), r.GetBoolean(21)) : null;
    }

    private List<(long Id, OutboxOperation Op, bool InFlight)> ReadEntries(SqliteTransaction tx, string itemId)
    {
        var entries = new List<(long, OutboxOperation, bool)>();
        using var cmd = Command(_connection, tx, "SELECT id, operation, in_flight FROM outbox WHERE item_id = $id ORDER BY id;", ("$id", itemId));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            entries.Add((r.GetInt64(0), Enum.Parse<OutboxOperation>(r.GetString(1)), r.GetBoolean(2)));
        }
        return entries;
    }

    /// <summary>
    /// Undoes the newest step (undo R4-R6). Every item is checked first and nothing is written if one is refused: an
    /// edited item must still be as the step left it (R6), a delete must not be in flight. Then, per item: an edit
    /// writes the earlier version back (verbatim while the item's create is unsent, else via <paramref name="restore"/>);
    /// an unsent delete is cancelled (an update it took over is put back); a sent one re-creates the item via
    /// <paramref name="restore"/> with no current item, which may return a new id (R8) or null when it can't (refused);
    /// a created item is deleted, removed outright while its create is unsent. <paramref name="restore"/> gets the
    /// earlier and the current version and must not call the store.
    /// </summary>
    public UndoResult UndoLast(Func<Item, Item?, Item?> restore)
    {
        UndoResult result;
        lock (_gate)
        {
            if (UndoSteps.Peek() is not { } step)
            {
                return new UndoResult(UndoOutcome.Nothing, null);
            }
            try
            {
                result = Revert(step, restore);
            }
            catch
            {
                // A step that fails to revert is dropped, so the next undo reaches the older ones.
                UndoSteps.Pop();
                throw;
            }
        }
        if (result.Outcome is UndoOutcome.Done or UndoOutcome.Recreated)
        {
            LocalChanged?.Invoke();
        }
        return result;
    }

    private UndoResult Revert(UndoStep step, Func<Item, Item?, Item?> restore)
    {
        if (step.Refusal is not null)
        {
            UndoSteps.Pop();
            return new UndoResult(UndoOutcome.Refused, step);
        }
        var outcome = UndoOutcome.Done;
        using var tx = _connection.BeginTransaction();

        // Check every item and work out what to write before writing anything.
        var plans = new List<(UndoChange Change, (Item Item, bool Deleted)? Row, List<(long Id, OutboxOperation Op, bool InFlight)> Entries, Item? Written)>();
        foreach (var change in step.Changes)
        {
            var row = ReadRow(tx, change.ItemId);
            var entries = ReadEntries(tx, change.ItemId);
            var unsentCreate = entries.Any(e => e.Op == OutboxOperation.Create) && !entries.Any(e => e.InFlight);
            Item? written = null;
            if (change.Before is not { } before)
            {
                // A create: deleted without a server check (duplicate-event interim); gone already is fine.
            }
            else if (change.After is null)
            {
                if (row is { Deleted: false })
                {
                    return Refuse(UndoOutcome.ChangedOnServer);
                }
                if (row is { } tombstone)
                {
                    if (entries.LastOrDefault() is not { Op: OutboxOperation.Delete } delete)
                    {
                        return Refuse(UndoOutcome.ChangedOnServer);
                    }
                    if (delete.InFlight)
                    {
                        return new UndoResult(UndoOutcome.Sending, step);
                    }
                    written = before with { Etag = tombstone.Item.Etag, RemoteId = tombstone.Item.RemoteId };
                }
                else if (restore(before, null) is { } recreated)
                {
                    written = recreated;
                    if (recreated.Id != change.ItemId)
                    {
                        outcome = UndoOutcome.Recreated;
                    }
                }
                else
                {
                    return Refuse(UndoOutcome.CannotRecreate);
                }
            }
            else
            {
                if (row is not { Deleted: false } live || live.Item.RawPayload != change.After.RawPayload)
                {
                    return Refuse(UndoOutcome.ChangedOnServer);
                }
                written = unsentCreate
                    ? before with { Etag = live.Item.Etag, RemoteId = live.Item.RemoteId }
                    : restore(before, live.Item) ?? throw new InvalidOperationException("No earlier version to write back.");
            }
            plans.Add((change, row, entries, written));
        }

        // Earlier versions back first, then the created items deleted (a move: the original back, then the copy goes).
        var group = step.Changes.Count > 1 ? Guid.NewGuid().ToString("N") : null;
        var window = OccurrenceWindow();
        foreach (var (change, row, entries, written) in plans.OrderBy(p => p.Change.Before is null))
        {
            if (change.Before is null)
            {
                DeleteCreated(tx, change.ItemId, row, entries, group);
                continue;
            }
            var item = written!;
            if (change.After is not null)
            {
                Execute(_connection, tx, UpdateItemSql, ItemParameters(tx, item with { Id = change.ItemId }));
                Enqueue(tx, change.ItemId, OutboxOperation.Update, item.RawPayload, row!.Value.Item.Etag, group);
            }
            else if (row is not null)
            {
                // Cancel the unsent delete.
                var delete = entries[^1].Id;
                Execute(_connection, tx, UpdateItemSql + " UPDATE items SET deleted = 0 WHERE id = $id;", ItemParameters(tx, item));
                Execute(_connection, tx, change.DeleteMergedUpdate
                    ? "UPDATE outbox SET operation = 'Update', payload = $p WHERE id = $entry;"
                    : "DELETE FROM outbox WHERE id = $entry;", ("$p", item.RawPayload), ("$entry", delete));
            }
            else
            {
                Execute(_connection, tx, InsertItemSql, ItemParameters(tx, item));
                Enqueue(tx, item.Id, OutboxOperation.Create, item.RawPayload, null, group);
            }
            RebuildItemOccurrences(tx, item.Id, window);
            IndexItem(_connection, tx, item.Id);
        }
        tx.Commit();
        UndoSteps.Pop();
        foreach (var (change, _, _, written) in plans)
        {
            if (change.Before is { } before && written is not null)
            {
                foreach (var older in UndoSteps.All)
                {
                    older.Rebase(change.ItemId, before.RawPayload, written);
                }
            }
        }
        return new UndoResult(outcome, step);

        UndoResult Refuse(UndoOutcome refused)
        {
            UndoSteps.Pop();
            return new UndoResult(refused, step);
        }
    }

    /// <summary>Undo of a create: never sent, the item and its entries go; else it is deleted on the server.</summary>
    private void DeleteCreated(SqliteTransaction tx, string itemId, (Item Item, bool Deleted)? row,
        List<(long Id, OutboxOperation Op, bool InFlight)> entries, string? group)
    {
        if (row is not { Deleted: false } live)
        {
            return;
        }
        if (entries.Any(e => e.Op == OutboxOperation.Create) && !entries.Any(e => e.InFlight))
        {
            Execute(_connection, tx,
                "DELETE FROM outbox WHERE item_id = $id; DELETE FROM occurrences WHERE item_id = $id; DELETE FROM items WHERE id = $id;",
                ("$id", itemId));
            return;
        }
        Execute(_connection, tx,
            "UPDATE items SET deleted = 1 WHERE id = $id; DELETE FROM occurrences WHERE item_id = $id;", ("$id", itemId));
        Enqueue(tx, itemId, OutboxOperation.Delete, null, live.Item.Etag, group);
    }
}
