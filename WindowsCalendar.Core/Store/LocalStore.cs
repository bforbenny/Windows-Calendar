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
            InsertAccount(null, account);
        }
    }

    public void AddCollection(Collection c)
    {
        lock (_gate)
        {
            InsertCollection(null, c);
        }
    }

    /// <summary>
    /// Saves a new account and the collections found on its server in one transaction. Each collection gets a
    /// local id, the next automatic colour and shape (ui-colour-theme R8) and reminders on. If no default calendar
    /// or task list is set yet, the server's main one (or else the first writable one) becomes the default
    /// (account-management R9).
    /// </summary>
    public IReadOnlyList<Collection> AddDiscoveredAccount(Account account, IReadOnlyList<DiscoveredCollection> found)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            InsertAccount(tx, account);

            var inUse = new HashSet<(string, string)>();
            using (var cmd = Command(_connection, tx, "SELECT colour, shape FROM collections;"))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    inUse.Add((r.GetString(0), r.GetString(1)));
                }
            }

            var added = new List<Collection>();
            foreach (var f in found)
            {
                var (colour, shape) = CollectionStyle.Next(inUse.Count, inUse);
                inUse.Add((colour, shape));
                var c = new Collection(Guid.NewGuid().ToString("N"), account.Id, f.Kind, f.Name, colour, shape, true, f.ReadOnly, f.RemoteRef);
                InsertCollection(tx, c);
                added.Add(c);
            }

            foreach (var (key, kind) in DefaultKeys)
            {
                // Writable collections of this kind, the server's main one first (OrderBy is stable).
                var pick = Enumerable.Range(0, found.Count)
                    .Where(i => found[i].Kind == kind && !found[i].ReadOnly)
                    .OrderByDescending(i => found[i].IsDefault)
                    .Select(i => added[i])
                    .FirstOrDefault();
                if (pick is not null && GetMeta(tx, key) is null)
                {
                    SetMeta(tx, key, pick.Id);
                }
            }
            tx.Commit();
            return added;
        }
    }

    public IReadOnlyList<Account> GetAccounts()
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null,
                "SELECT id, type, display_name, server_url, username, email FROM accounts ORDER BY rowid;");
            using var r = cmd.ExecuteReader();
            var list = new List<Account>();
            while (r.Read())
            {
                list.Add(new Account(r.GetString(0), Enum.Parse<AccountType>(r.GetString(1)), r.GetString(2),
                    NullableString(r, 3), NullableString(r, 4), NullableString(r, 5)));
            }
            return list;
        }
    }

    /// <summary>An account's collections in the order they were added (sidebar order).</summary>
    public IReadOnlyList<Collection> GetCollections(string accountId)
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null,
                """
                SELECT id, account_id, kind, name, colour, shape, reminders_on, read_only, remote_ref
                FROM collections WHERE account_id = $a ORDER BY rowid;
                """,
                ("$a", accountId));
            using var r = cmd.ExecuteReader();
            var list = new List<Collection>();
            while (r.Read())
            {
                list.Add(new Collection(r.GetString(0), r.GetString(1), Enum.Parse<CollectionKind>(r.GetString(2)), r.GetString(3),
                    r.GetString(4), r.GetString(5), r.GetBoolean(6), r.GetBoolean(7), r.GetString(8)));
            }
            return list;
        }
    }

    /// <summary>Default calendar for new events (account-management R9); null when there is none.</summary>
    public string? DefaultCalendarId
    {
        get { lock (_gate) { return GetMeta(null, "default_calendar"); } }
    }

    /// <summary>Default task list for new tasks (account-management R9); null when there is none.</summary>
    public string? DefaultTaskListId
    {
        get { lock (_gate) { return GetMeta(null, "default_tasklist"); } }
    }

    /// <summary>
    /// Deletes an account and everything cached for it, including unsent changes (account-management R8).
    /// The caller confirms first and removes the account's credentials. A default that pointed into the account
    /// moves to the next writable collection in sidebar order.
    /// </summary>
    public void RemoveAccount(string accountId)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            // The outbox has no foreign key to items, so its entries go first; the rest cascades.
            Execute(_connection, tx,
                """
                DELETE FROM outbox WHERE item_id IN (
                    SELECT i.id FROM items i JOIN collections c ON c.id = i.collection_id WHERE c.account_id = $a);
                DELETE FROM accounts WHERE id = $a;
                """,
                ("$a", accountId));

            foreach (var (key, kind) in DefaultKeys)
            {
                using (var exists = Command(_connection, tx,
                    "SELECT 1 FROM collections WHERE id = (SELECT value FROM meta WHERE key = $key);", ("$key", key)))
                {
                    if (exists.ExecuteScalar() is not null)
                    {
                        continue;
                    }
                }

                using var first = Command(_connection, tx,
                    "SELECT id FROM collections WHERE kind = $kind AND read_only = 0 ORDER BY rowid LIMIT 1;",
                    ("$kind", kind.ToString()));
                if (first.ExecuteScalar() is string next)
                {
                    SetMeta(tx, key, next);
                }
                else
                {
                    Execute(_connection, tx, "DELETE FROM meta WHERE key = $key;", ("$key", key));
                }
            }
            tx.Commit();
        }
    }

    // ---- Items: local writes (R5) ----

    public void CreateItem(Item item, string? groupId = null)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            Execute(_connection, tx, InsertItemSql, ItemParameters(item));
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
            Execute(_connection, tx, UpdateItemSql, ItemParameters(item));
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

    // ---- Server changes (sync engines). Never queued in the outbox. ----

    /// <summary>Etags of the cached items with a remote id in the given collections, keyed by remote id (href).</summary>
    public IReadOnlyDictionary<string, string?> GetRemoteEtags(IReadOnlyList<string> collectionIds)
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null,
                $"SELECT remote_id, etag FROM items WHERE remote_id IS NOT NULL AND collection_id IN ({InList(collectionIds)});",
                InParameters(collectionIds));
            using var r = cmd.ExecuteReader();
            var etags = new Dictionary<string, string?>();
            while (r.Read())
            {
                etags[r.GetString(0)] = NullableString(r, 1);
            }
            return etags;
        }
    }

    /// <summary>
    /// Inserts or replaces the item with this remote id in the given collections, and rebuilds its occurrences.
    /// Returns false, changing nothing, if the item has unsent local changes (conflict-resolution R1; the
    /// Conflicts table comes later).
    /// </summary>
    public bool ApplyServerItem(IReadOnlyList<string> collectionIds, Item item)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var existingId = FindByRemoteId(tx, collectionIds, item.RemoteId!);
            if (existingId is not null && HasOutboxEntries(tx, existingId))
            {
                return false;
            }
            if (existingId is null)
            {
                Execute(_connection, tx, InsertItemSql, ItemParameters(item));
            }
            else
            {
                item = item with { Id = existingId };
                Execute(_connection, tx, UpdateItemSql, ItemParameters(item));
            }
            RebuildItemOccurrences(tx, item.Id, OccurrenceWindow());
            tx.Commit();
            return true;
        }
    }

    /// <summary>
    /// Deletes the item the server removed. Returns false, keeping it, if it has unsent local changes.
    /// </summary>
    public bool RemoveServerItem(IReadOnlyList<string> collectionIds, string remoteId)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var id = FindByRemoteId(tx, collectionIds, remoteId);
            if (id is null)
            {
                return true;
            }
            if (HasOutboxEntries(tx, id))
            {
                return false;
            }
            Execute(_connection, tx, "DELETE FROM items WHERE id = $id;", ("$id", id));
            tx.Commit();
            return true;
        }
    }

    // ---- Internals ----

    private const string InsertItemSql =
        """
        INSERT INTO items (id, collection_id, kind, remote_id, raw_payload, summary, start_utc, end_utc, all_day,
            time_zone, location, status, due_utc, completed_utc, is_recurring, etag, last_modified)
        VALUES ($id, $collection, $kind, $remote, $raw, $summary, $start, $end, $allDay,
            $tz, $location, $status, $due, $completed, $recurring, $etag, $modified);
        """;

    private const string UpdateItemSql =
        """
        UPDATE items SET collection_id = $collection, kind = $kind, remote_id = $remote, raw_payload = $raw,
            summary = $summary, start_utc = $start, end_utc = $end, all_day = $allDay, time_zone = $tz,
            location = $location, status = $status, due_utc = $due, completed_utc = $completed,
            is_recurring = $recurring, etag = $etag, last_modified = $modified
        WHERE id = $id;
        """;

    private string? FindByRemoteId(SqliteTransaction tx, IReadOnlyList<string> collectionIds, string remoteId)
    {
        using var cmd = Command(_connection, tx,
            $"SELECT id FROM items WHERE remote_id = $remote AND collection_id IN ({InList(collectionIds)});",
            [("$remote", remoteId), .. InParameters(collectionIds)]);
        return cmd.ExecuteScalar() as string;
    }

    private bool HasOutboxEntries(SqliteTransaction tx, string itemId)
    {
        using var cmd = Command(_connection, tx, "SELECT 1 FROM outbox WHERE item_id = $id LIMIT 1;", ("$id", itemId));
        return cmd.ExecuteScalar() is not null;
    }

    private static string InList(IReadOnlyList<string> values) => string.Join(", ", values.Select((_, i) => $"$in{i}"));

    private static (string, object?)[] InParameters(IReadOnlyList<string> values) =>
        values.Select((v, i) => ($"$in{i}", (object?)v)).ToArray();

    private static readonly (string Key, CollectionKind Kind)[] DefaultKeys =
        [("default_calendar", CollectionKind.Calendar), ("default_tasklist", CollectionKind.TaskList)];

    private void InsertAccount(SqliteTransaction? tx, Account a) =>
        Execute(_connection, tx,
            """
            INSERT INTO accounts (id, type, display_name, server_url, username, email)
            VALUES ($id, $type, $name, $url, $user, $email);
            """,
            ("$id", a.Id), ("$type", a.Type.ToString()), ("$name", a.DisplayName), ("$url", a.ServerUrl),
            ("$user", a.Username), ("$email", a.Email));

    private void InsertCollection(SqliteTransaction? tx, Collection c) =>
        Execute(_connection, tx,
            """
            INSERT INTO collections (id, account_id, kind, name, colour, shape, reminders_on, read_only, remote_ref)
            VALUES ($id, $account, $kind, $name, $colour, $shape, $reminders, $readOnly, $remote);
            """,
            ("$id", c.Id), ("$account", c.AccountId), ("$kind", c.Kind.ToString()), ("$name", c.Name),
            ("$colour", c.Colour), ("$shape", c.Shape), ("$reminders", c.RemindersOn), ("$readOnly", c.ReadOnly),
            ("$remote", c.RemoteRef));

    private string? GetMeta(SqliteTransaction? tx, string key)
    {
        using var cmd = Command(_connection, tx, "SELECT value FROM meta WHERE key = $key;", ("$key", key));
        return cmd.ExecuteScalar() as string;
    }

    private void SetMeta(SqliteTransaction? tx, string key, string value) =>
        Execute(_connection, tx,
            """
            INSERT INTO meta (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value;
            """,
            ("$key", key), ("$value", value));

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
