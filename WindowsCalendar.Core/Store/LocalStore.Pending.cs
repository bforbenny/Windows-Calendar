using Microsoft.Data.Sqlite;

namespace WindowsCalendar.Core.Store;

/// <summary>
/// The pending changes list (local-store-offline R9a) with Discard (R7, conflict-resolution R9), and the per-account
/// "synced once" mark behind the first-sync text (ui-states R1a).
/// </summary>
public sealed partial class LocalStore
{
    private const string SyncedOnceKey = "synced-once:";

    /// <summary>Every outbox entry in replay order, with its item's title for the list.</summary>
    public IReadOnlyList<PendingChange> GetPendingChanges()
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null,
                """
                SELECT o.id, o.item_id, o.operation, o.last_error, o.permanent, o.in_flight, i.summary, i.raw_payload, i.kind
                FROM outbox o LEFT JOIN items i ON i.id = o.item_id
                ORDER BY o.id;
                """);
            using var r = cmd.ExecuteReader();
            var list = new List<PendingChange>();
            while (r.Read())
            {
                var title = NullableString(r, 6) ?? PayloadTitle(NullableString(r, 7));
                list.Add(new PendingChange(r.GetInt64(0), r.GetString(1), string.IsNullOrWhiteSpace(title) ? "(No title)" : title,
                    Enum.Parse<OutboxOperation>(r.GetString(2)), NullableString(r, 3), r.GetBoolean(4), r.GetBoolean(5),
                    NullableString(r, 8) is { } kind ? Enum.Parse<ItemKind>(kind) : ItemKind.Event));
            }
            return list;
        }
    }

    /// <summary>
    /// Discard of a failed entry (R7): every queued entry for its item is dropped and the local version goes to
    /// Conflicts. An item the server never had leaves the calendar. One the server has stays as it is locally until the
    /// next sync fetches the server's version again: its etag is cleared and its collection's sync state is dropped, so
    /// the engine reads that collection in full once. A linked group's later entries are cancelled, as for a conflict.
    /// An entry being sent is left alone. Returns false when nothing was discarded.
    /// </summary>
    public bool DiscardOutboxEntry(long entryId)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            string? itemId = null, group = null;
            using (var cmd = Command(_connection, tx, "SELECT item_id, group_id FROM outbox WHERE id = $id AND in_flight = 0;", ("$id", entryId)))
            using (var r = cmd.ExecuteReader())
            {
                if (r.Read())
                {
                    (itemId, group) = (r.GetString(0), NullableString(r, 1));
                }
            }
            if (itemId is null)
            {
                return false;
            }
            string? remoteId = null, collectionId = null;
            using (var cmd = Command(_connection, tx, "SELECT remote_id, collection_id FROM items WHERE id = $id;", ("$id", itemId)))
            using (var r = cmd.ExecuteReader())
            {
                if (r.Read())
                {
                    (remoteId, collectionId) = (NullableString(r, 0), r.GetString(1));
                }
            }
            Execute(_connection, tx,
                """
                INSERT INTO conflicts (item_id, collection_id, kind, local_payload, server_payload, detected_at)
                SELECT id, collection_id, kind, CASE WHEN deleted = 1 THEN NULL ELSE raw_payload END, NULL, $now
                FROM items WHERE id = $id;
                DELETE FROM outbox WHERE item_id = $id;
                """,
                ("$id", itemId), ("$now", _time.GetUtcNow().ToUnixTimeMilliseconds()));
            if (collectionId is not null && remoteId is null)
            {
                Execute(_connection, tx, "DELETE FROM occurrences WHERE item_id = $id; DELETE FROM items WHERE id = $id;", ("$id", itemId));
            }
            else if (collectionId is not null)
            {
                // The same server collection may back more than one local one (a CalDAV calendar and task list on one URL).
                Execute(_connection, tx,
                    """
                    UPDATE items SET deleted = 0, etag = NULL WHERE id = $id;
                    DELETE FROM sync_state WHERE collection_id IN (
                        SELECT c.id FROM collections c JOIN collections o ON o.id = $c
                        WHERE c.account_id = o.account_id AND c.remote_ref = o.remote_ref);
                    """,
                    ("$id", itemId), ("$c", collectionId));
                RebuildItemOccurrences(tx, itemId, OccurrenceWindow());
                IndexItem(_connection, tx, itemId);
            }
            if (group is not null)
            {
                CancelGroup(tx, group);
            }
            tx.Commit();
            return true;
        }
    }

    /// <summary>
    /// Whether the account has finished a sync run at least once (ui-states R1a). An account that already has items
    /// counts, so accounts added before the mark existed do not look new.
    /// </summary>
    public bool HasSyncedOnce(string accountId)
    {
        lock (_gate)
        {
            if (GetMeta(null, SyncedOnceKey + accountId) is not null)
            {
                return true;
            }
            using var cmd = Command(_connection, null,
                "SELECT EXISTS (SELECT 1 FROM items i JOIN collections c ON c.id = i.collection_id WHERE c.account_id = $a);",
                ("$a", accountId));
            return Convert.ToBoolean(cmd.ExecuteScalar());
        }
    }

    public void MarkSyncedOnce(string accountId)
    {
        lock (_gate)
        {
            SetMeta(null, SyncedOnceKey + accountId, "1");
        }
    }
}
