using Microsoft.Data.Sqlite;

namespace WindowsCalendar.Core.Store;

/// <summary>
/// Local SQLite store and outbox (local-store-offline). One connection, guarded by a lock;
/// calls are synchronous and short. Every local write changes the item and the outbox in one
/// transaction (R5).
/// </summary>
public sealed class LocalStore : IDisposable
{
    /// <summary>First retry delay; each later attempt doubles it (R7).</summary>
    public static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Longest retry delay (R7).</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    private const string ItemColumns =
        """
        i.id, i.collection_id, i.kind, i.remote_id, i.raw_payload, i.summary, i.start_utc, i.end_utc, i.all_day, i.time_zone,
        i.location, i.status, i.due_utc, i.completed_utc, i.is_recurring, i.etag, i.last_modified
        """;

    private const string OutboxColumns =
        "o.id, o.item_id, o.operation, o.payload, o.base_etag, o.created_at, o.attempts, o.last_error, o.group_id, o.in_flight, o.next_attempt_at, o.permanent";

    private readonly SqliteConnection _connection;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private LocalStore(SqliteConnection connection, TimeProvider time)
    {
        _connection = connection;
        _time = time;
    }

    /// <summary>Raised after a local create, update or delete is committed (R10: sync after a local change).</summary>
    public event Action? LocalChanged;

    /// <summary>
    /// Opens (or creates) the store and applies migrations.
    /// Throws <see cref="StoreTooNewException"/> without changing anything if the store is newer than this build.
    /// </summary>
    public static LocalStore Open(string path, TimeProvider? time = null)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            connection.Open();
            Migrations.Apply(connection);
            Execute(connection, null, "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;");
            // Entries left in flight by a crash are sent again (R4).
            Execute(connection, null, "UPDATE outbox SET in_flight = 0 WHERE in_flight = 1;");
            return new LocalStore(connection, time ?? TimeProvider.System);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose() => _connection.Dispose();

    // ---- Accounts and collections ----

    public void AddAccount(Account account)
    {
        lock (_gate)
        {
            Execute(_connection, null,
                "INSERT INTO accounts (id, type, display_name) VALUES ($id, $type, $name);",
                ("$id", account.Id), ("$type", account.Type.ToString()), ("$name", account.DisplayName));
        }
    }

    public void AddCollection(Collection c)
    {
        lock (_gate)
        {
            Execute(_connection, null,
                """
                INSERT INTO collections (id, account_id, kind, name, colour, shape, reminders_on, read_only, remote_ref)
                VALUES ($id, $account, $kind, $name, $colour, $shape, $reminders, $readOnly, $remote);
                """,
                ("$id", c.Id), ("$account", c.AccountId), ("$kind", c.Kind.ToString()), ("$name", c.Name),
                ("$colour", c.Colour), ("$shape", c.Shape), ("$reminders", c.RemindersOn), ("$readOnly", c.ReadOnly),
                ("$remote", c.RemoteRef));
        }
    }

    // ---- Items: local writes (R5) ----

    public void CreateItem(Item item, string? groupId = null)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            Execute(_connection, tx,
                """
                INSERT INTO items (id, collection_id, kind, remote_id, raw_payload, summary, start_utc, end_utc, all_day,
                    time_zone, location, status, due_utc, completed_utc, is_recurring, etag, last_modified)
                VALUES ($id, $collection, $kind, $remote, $raw, $summary, $start, $end, $allDay,
                    $tz, $location, $status, $due, $completed, $recurring, $etag, $modified);
                """,
                ItemParameters(item));
            RebuildItemOccurrences(tx, item.Id, OccurrenceWindow());
            Enqueue(tx, item.Id, OutboxOperation.Create, item.RawPayload, null, groupId);
            tx.Commit();
        }
        LocalChanged?.Invoke();
    }

    public void UpdateItem(Item item, string? groupId = null)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var baseEtag = GetLiveEtag(tx, item.Id);
            Execute(_connection, tx,
                """
                UPDATE items SET collection_id = $collection, kind = $kind, remote_id = $remote, raw_payload = $raw,
                    summary = $summary, start_utc = $start, end_utc = $end, all_day = $allDay, time_zone = $tz,
                    location = $location, status = $status, due_utc = $due, completed_utc = $completed,
                    is_recurring = $recurring, etag = $etag, last_modified = $modified
                WHERE id = $id;
                """,
                ItemParameters(item));
            RebuildItemOccurrences(tx, item.Id, OccurrenceWindow());
            Enqueue(tx, item.Id, OutboxOperation.Update, item.RawPayload, baseEtag, groupId);
            tx.Commit();
        }
        LocalChanged?.Invoke();
    }

    /// <summary>
    /// Deletes locally. The row stays as a hidden tombstone until the server delete completes (R2),
    /// unless the item was never sent, in which case it is removed outright.
    /// </summary>
    public void DeleteItem(string itemId, string? groupId = null)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var baseEtag = GetLiveEtag(tx, itemId);
            Execute(_connection, tx,
                "UPDATE items SET deleted = 1 WHERE id = $id; DELETE FROM occurrences WHERE item_id = $id;", ("$id", itemId));
            Enqueue(tx, itemId, OutboxOperation.Delete, null, baseEtag, groupId);
            tx.Commit();
        }
        LocalChanged?.Invoke();
    }

    /// <summary>Returns the item, or null if it does not exist or is deleted.</summary>
    public Item? GetItem(string itemId)
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null,
                $"SELECT {ItemColumns} FROM items i WHERE i.id = $id AND i.deleted = 0;", ("$id", itemId));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadItem(r) : null;
        }
    }

    // ---- Occurrence index (R3) ----

    /// <summary>Occurrences of live items that overlap [from, to), ordered by start.</summary>
    public IReadOnlyList<ItemOccurrence> GetOccurrences(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null,
                """
                SELECT o.item_id, o.start_utc, o.end_utc FROM occurrences o JOIN items i ON i.id = o.item_id
                WHERE i.deleted = 0 AND o.start_utc < $to AND o.end_utc >= $from
                ORDER BY o.start_utc, o.item_id;
                """,
                ("$from", from.ToUnixTimeMilliseconds()), ("$to", to.ToUnixTimeMilliseconds()));
            using var r = cmd.ExecuteReader();
            var list = new List<ItemOccurrence>();
            while (r.Read())
            {
                list.Add(new ItemOccurrence(r.GetString(0), Time(r, 1), Time(r, 2)));
            }
            return list;
        }
    }

    /// <summary>
    /// Rebuilds the whole index if the day or the Windows timezone changed since the last full build (rolling
    /// window; timezone-handling R6). Returns true if it rebuilt. Call TimeZoneInfo.ClearCachedData first so a
    /// timezone change is seen.
    /// </summary>
    public bool RefreshOccurrences()
    {
        lock (_gate)
        {
            var window = OccurrenceWindow();
            var stamp = $"{window.Today:yyyy-MM-dd}|{window.Zone.Id}";
            using var tx = _connection.BeginTransaction();
            using (var cmd = Command(_connection, tx, "SELECT value FROM meta WHERE key = 'occurrences_built';"))
            {
                if (cmd.ExecuteScalar() as string == stamp)
                {
                    return false;
                }
            }

            var ids = new List<string>();
            using (var cmd = Command(_connection, tx, "SELECT id FROM items WHERE deleted = 0;"))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    ids.Add(r.GetString(0));
                }
            }
            foreach (var id in ids)
            {
                RebuildItemOccurrences(tx, id, window);
            }
            Execute(_connection, tx,
                """
                INSERT INTO meta (key, value) VALUES ('occurrences_built', $v)
                ON CONFLICT (key) DO UPDATE SET value = excluded.value;
                """,
                ("$v", stamp));
            tx.Commit();
            return true;
        }
    }

    // ---- Outbox: sync engine side (R4, R7) ----

    /// <summary>All entries in replay order.</summary>
    public IReadOnlyList<OutboxEntry> GetOutbox()
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null, $"SELECT {OutboxColumns} FROM outbox o ORDER BY o.id;");
            return ReadOutbox(cmd);
        }
    }

    /// <summary>
    /// Entries ready to send now, in replay order: not in flight, not permanently failed, past their retry time,
    /// and with no earlier entry still queued for the same item or the same linked group (R4, R7).
    /// </summary>
    public IReadOnlyList<OutboxEntry> GetDueOutbox()
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null,
                $"""
                SELECT {OutboxColumns} FROM outbox o
                WHERE o.in_flight = 0 AND o.permanent = 0 AND o.next_attempt_at <= $now
                  AND NOT EXISTS (SELECT 1 FROM outbox p WHERE p.id < o.id
                      AND (p.item_id = o.item_id OR (o.group_id IS NOT NULL AND p.group_id = o.group_id)))
                ORDER BY o.id;
                """,
                ("$now", _time.GetUtcNow().ToUnixTimeMilliseconds()));
            return ReadOutbox(cmd);
        }
    }

    /// <summary>Marks an entry as being sent; later local changes to the item get their own entry.</summary>
    public void MarkInFlight(long entryId)
    {
        lock (_gate)
        {
            Execute(_connection, null, "UPDATE outbox SET in_flight = 1 WHERE id = $id;", ("$id", entryId));
        }
    }

    /// <summary>Removes a sent entry. A completed delete also removes the item's tombstone.</summary>
    public void CompleteOutboxEntry(long entryId)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            Execute(_connection, tx,
                """
                DELETE FROM items WHERE deleted = 1 AND id = (SELECT item_id FROM outbox WHERE id = $id AND operation = 'Delete');
                DELETE FROM outbox WHERE id = $id;
                """,
                ("$id", entryId));
            tx.Commit();
        }
    }

    /// <summary>
    /// Records a failed send; the entry stays queued. A transient failure is retried after <see cref="RetryDelay"/>;
    /// a permanent one waits for <see cref="RetryOutboxEntry"/> (R7).
    /// </summary>
    public void RecordOutboxFailure(long entryId, string error, bool permanent = false)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            int attempts;
            using (var cmd = Command(_connection, tx, "SELECT attempts FROM outbox WHERE id = $id;", ("$id", entryId)))
            {
                attempts = Convert.ToInt32(cmd.ExecuteScalar()) + 1;
            }
            var next = _time.GetUtcNow() + RetryDelay(attempts);
            Execute(_connection, tx,
                """
                UPDATE outbox SET attempts = $attempts, last_error = $error, in_flight = 0, next_attempt_at = $next,
                    permanent = $permanent
                WHERE id = $id;
                """,
                ("$id", entryId), ("$attempts", attempts), ("$error", error), ("$next", next.ToUnixTimeMilliseconds()),
                ("$permanent", permanent));
            tx.Commit();
        }
    }

    /// <summary>User Retry on a failed entry (R7, R9a): it is sent at the next sync.</summary>
    public void RetryOutboxEntry(long entryId)
    {
        lock (_gate)
        {
            Execute(_connection, null, "UPDATE outbox SET permanent = 0, next_attempt_at = 0 WHERE id = $id;", ("$id", entryId));
        }
    }

    /// <summary>Sync now: transient failures become due immediately (R7). Permanent failures still need Retry.</summary>
    public void ResetBackoff()
    {
        lock (_gate)
        {
            Execute(_connection, null, "UPDATE outbox SET next_attempt_at = 0 WHERE permanent = 0;");
        }
    }

    /// <summary>Delay before retrying after the given number of failed attempts: 30 s, 1, 2, 4, 8 min, then 15 min (R7).</summary>
    public static TimeSpan RetryDelay(int attempts)
    {
        var delay = FirstRetryDelay * Math.Pow(2, Math.Min(attempts - 1, 10));
        return delay < MaxRetryDelay ? delay : MaxRetryDelay;
    }

    // ---- Sync state (R8) ----

    public string? GetSyncState(string collectionId, string key)
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null,
                "SELECT value FROM sync_state WHERE collection_id = $c AND key = $k;", ("$c", collectionId), ("$k", key));
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetSyncState(string collectionId, string key, string value)
    {
        lock (_gate)
        {
            Execute(_connection, null,
                """
                INSERT INTO sync_state (collection_id, key, value) VALUES ($c, $k, $v)
                ON CONFLICT (collection_id, key) DO UPDATE SET value = excluded.value;
                """,
                ("$c", collectionId), ("$k", key), ("$v", value));
        }
    }

    // ---- Internals ----

    /// <summary>
    /// Adds an outbox entry, coalescing with the item's latest entry when that entry is not grouped
    /// and not in flight, and the new change is not grouped (R4).
    /// </summary>
    private void Enqueue(SqliteTransaction tx, string itemId, OutboxOperation op, string? payload, string? baseEtag, string? groupId)
    {
        if (groupId is null)
        {
            using var cmd = Command(_connection, tx,
                "SELECT id, operation, group_id, in_flight FROM outbox WHERE item_id = $item ORDER BY id DESC LIMIT 1;",
                ("$item", itemId));
            using var r = cmd.ExecuteReader();
            if (r.Read() && r.IsDBNull(2) && !r.GetBoolean(3))
            {
                var lastId = r.GetInt64(0);
                var lastOp = Enum.Parse<OutboxOperation>(r.GetString(1));
                r.Close();

                switch (lastOp, op)
                {
                    case (OutboxOperation.Create, OutboxOperation.Update):
                    case (OutboxOperation.Update, OutboxOperation.Update):
                        Execute(_connection, tx, "UPDATE outbox SET payload = $p WHERE id = $id;", ("$p", payload), ("$id", lastId));
                        return;
                    case (OutboxOperation.Create, OutboxOperation.Delete):
                        // Never reached the server: drop the entry and the item.
                        Execute(_connection, tx, "DELETE FROM outbox WHERE id = $id; DELETE FROM items WHERE id = $item;",
                            ("$id", lastId), ("$item", itemId));
                        return;
                    case (OutboxOperation.Update, OutboxOperation.Delete):
                        Execute(_connection, tx, "UPDATE outbox SET operation = 'Delete', payload = NULL WHERE id = $id;", ("$id", lastId));
                        return;
                }
            }
        }

        Execute(_connection, tx,
            """
            INSERT INTO outbox (item_id, operation, payload, base_etag, created_at, group_id)
            VALUES ($item, $op, $payload, $etag, $created, $group);
            """,
            ("$item", itemId), ("$op", op.ToString()), ("$payload", payload), ("$etag", baseEtag),
            ("$created", _time.GetUtcNow().ToUnixTimeMilliseconds()), ("$group", groupId));
    }

    /// <summary>Etag of a live (not deleted) item; throws if there is none.</summary>
    private string? GetLiveEtag(SqliteTransaction tx, string itemId)
    {
        using var cmd = Command(_connection, tx, "SELECT etag FROM items WHERE id = $id AND deleted = 0;", ("$id", itemId));
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            throw new KeyNotFoundException($"Item {itemId} does not exist.");
        }
        return NullableString(r, 0);
    }

    /// <summary>The index covers -3 / +12 months from today (UTC), in the current Windows timezone (R3).</summary>
    private (DateTimeOffset Today, DateTimeOffset Start, DateTimeOffset End, TimeZoneInfo Zone) OccurrenceWindow()
    {
        var today = new DateTimeOffset(_time.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);
        return (today, today.AddMonths(-3), today.AddMonths(12), _time.LocalTimeZone);
    }

    /// <summary>Replaces one item's rows in the occurrence index (R3).</summary>
    private void RebuildItemOccurrences(SqliteTransaction tx, string itemId,
        (DateTimeOffset Today, DateTimeOffset Start, DateTimeOffset End, TimeZoneInfo Zone) window)
    {
        Item item;
        AccountType accountType;
        using (var cmd = Command(_connection, tx,
            $"""
            SELECT {ItemColumns}, a.type FROM items i
            JOIN collections c ON c.id = i.collection_id JOIN accounts a ON a.id = c.account_id
            WHERE i.id = $id;
            """,
            ("$id", itemId)))
        using (var r = cmd.ExecuteReader())
        {
            r.Read();
            item = ReadItem(r);
            accountType = Enum.Parse<AccountType>(r.GetString(17));
        }

        Execute(_connection, tx, "DELETE FROM occurrences WHERE item_id = $id;", ("$id", itemId));
        foreach (var (start, end) in OccurrenceExpander.Expand(item, accountType, window.Start, window.End, window.Zone))
        {
            Execute(_connection, tx, "INSERT INTO occurrences (item_id, start_utc, end_utc) VALUES ($id, $s, $e);",
                ("$id", itemId), ("$s", start.ToUnixTimeMilliseconds()), ("$e", end.ToUnixTimeMilliseconds()));
        }
    }

    private static List<OutboxEntry> ReadOutbox(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var entries = new List<OutboxEntry>();
        while (r.Read())
        {
            entries.Add(new OutboxEntry(
                r.GetInt64(0), r.GetString(1), Enum.Parse<OutboxOperation>(r.GetString(2)), NullableString(r, 3),
                NullableString(r, 4), Time(r, 5), r.GetInt32(6), NullableString(r, 7), NullableString(r, 8),
                r.GetBoolean(9), Time(r, 10), r.GetBoolean(11)));
        }
        return entries;
    }

    /// <summary>Reads the <see cref="ItemColumns"/> at the start of a row.</summary>
    private static Item ReadItem(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), Enum.Parse<ItemKind>(r.GetString(2)), NullableString(r, 3), r.GetString(4),
        NullableString(r, 5), NullableTime(r, 6), NullableTime(r, 7), r.GetBoolean(8), NullableString(r, 9),
        NullableString(r, 10), NullableString(r, 11), NullableTime(r, 12), NullableTime(r, 13), r.GetBoolean(14),
        NullableString(r, 15), NullableTime(r, 16));

    private static (string, object?)[] ItemParameters(Item i) =>
    [
        ("$id", i.Id), ("$collection", i.CollectionId), ("$kind", i.Kind.ToString()), ("$remote", i.RemoteId),
        ("$raw", i.RawPayload), ("$summary", i.Summary), ("$start", i.StartUtc?.ToUnixTimeMilliseconds()),
        ("$end", i.EndUtc?.ToUnixTimeMilliseconds()), ("$allDay", i.AllDay), ("$tz", i.TimeZone),
        ("$location", i.Location), ("$status", i.Status), ("$due", i.DueUtc?.ToUnixTimeMilliseconds()),
        ("$completed", i.CompletedUtc?.ToUnixTimeMilliseconds()), ("$recurring", i.IsRecurring), ("$etag", i.Etag),
        ("$modified", i.LastModifiedUtc?.ToUnixTimeMilliseconds()),
    ];

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return cmd;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(connection, tx, sql, parameters);
        cmd.ExecuteNonQuery();
    }

    private static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset Time(SqliteDataReader r, int i) => DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(i));

    private static DateTimeOffset? NullableTime(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(i));
}
