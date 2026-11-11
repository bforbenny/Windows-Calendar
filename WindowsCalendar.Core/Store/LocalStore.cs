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
        i.location, i.status, i.due_utc, i.completed_utc, i.is_recurring, i.etag, i.last_modified, i.series_id, i.read_only, i.unaccepted,
        i.join_url
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
            // After v11 and v12: columns that older versions did not fill are read from the stored events once.
            Remap(connection, "remap-show-as", "status", "raw_payload NOT LIKE '{%'",
                payload => ShowAsValues.ToGraph(CalDav.CalDavEvents.ReadShowAs(CalDav.CalDavEvents.Master(Ical.Net.Calendar.Load(payload)!))));
            Remap(connection, "remap-join-url", "join_url", "1 = 1", payload => JoinLink.FromPayload(payload)?.Url);
            BuildSearchIndex(connection);
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

    /// <summary>
    /// When the meta flag <paramref name="key"/> is set (by a migration): sets <paramref name="column"/> of the stored
    /// events matching <paramref name="filter"/> from their payload, then clears the flag. Both names are constants.
    /// </summary>
    private static void Remap(SqliteConnection connection, string key, string column, string filter, Func<string, string?> derive)
    {
        using (var check = Command(connection, null, "SELECT 1 FROM meta WHERE key = $key;", ("$key", key)))
        {
            if (check.ExecuteScalar() is null)
            {
                return;
            }
        }
        using var tx = connection.BeginTransaction();
        var rows = new List<(string Id, string Payload)>();
        using (var cmd = Command(connection, tx, $"SELECT id, raw_payload FROM items WHERE kind = 'Event' AND {filter};"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                rows.Add((r.GetString(0), r.GetString(1)));
            }
        }
        foreach (var (id, payload) in rows)
        {
            string? value;
            try
            {
                value = derive(payload);
            }
            catch (Exception)
            {
                continue;
            }
            Execute(connection, tx, $"UPDATE items SET {column} = $value WHERE id = $id;", ("$value", value), ("$id", id));
        }
        Execute(connection, tx, "DELETE FROM meta WHERE key = $key;", ("$key", key));
        tx.Commit();
    }

    /// <summary>After v13: indexes every stored item for search once (search R1), then clears the flag.</summary>
    private static void BuildSearchIndex(SqliteConnection connection)
    {
        using (var check = Command(connection, null, "SELECT 1 FROM meta WHERE key = 'build-search';"))
        {
            if (check.ExecuteScalar() is null)
            {
                return;
            }
        }
        using var tx = connection.BeginTransaction();
        var ids = new List<string>();
        using (var cmd = Command(connection, tx, "SELECT id FROM items;"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                ids.Add(r.GetString(0));
            }
        }
        Execute(connection, tx, "DELETE FROM search_index;");
        foreach (var id in ids)
        {
            IndexItem(connection, tx, id);
        }
        Execute(connection, tx, "DELETE FROM meta WHERE key = 'build-search';");
        tx.Commit();
    }

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
    /// local id, its snapped server colour or else the next automatic colour, the next automatic shape
    /// (ui-colour-theme R8, R9) and reminders on. If no default calendar
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
                var server = CalendarColours.Snap(f.ServerColour);
                var c = new Collection(Guid.NewGuid().ToString("N"), account.Id, f.Kind, f.Name, server ?? colour, shape, true, f.ReadOnly,
                    f.RemoteRef, f.SourceUrl, ServerColour: server);
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
                SELECT id, account_id, kind, name, colour, shape, reminders_on, read_only, remote_ref, source_url, colour_override, server_colour
                FROM collections WHERE account_id = $a ORDER BY rowid;
                """,
                ("$a", accountId));
            using var r = cmd.ExecuteReader();
            var list = new List<Collection>();
            while (r.Read())
            {
                list.Add(new Collection(r.GetString(0), r.GetString(1), Enum.Parse<CollectionKind>(r.GetString(2)), r.GetString(3),
                    r.GetString(4), r.GetString(5), r.GetBoolean(6), r.GetBoolean(7), r.GetString(8), NullableString(r, 9),
                    r.GetBoolean(10), NullableString(r, 11)));
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
                DELETE FROM meta WHERE key IN (SELECT 'hidden:' || id FROM collections WHERE account_id = $a);
                DELETE FROM accounts WHERE id = $a;
                """,
                ("$a", accountId));
            ReassignDefaults(tx);
            tx.Commit();
        }
    }

    /// <summary>
    /// Merges a fresh collection listing into an account (account-management R11), matching by URL and kind. New
    /// collections get the next automatic colour and shape; names, read-only flags, feed URLs and server colours
    /// (snapped, ui-colour-theme R9) follow the server.
    /// A collection no longer listed is removed with its items; its unsent changes are kept in Conflicts first
    /// (conflict-resolution R9). An empty listing removes nothing.
    /// </summary>
    public CollectionChanges SyncCollections(string accountId, IReadOnlyList<DiscoveredCollection> found)
    {
        var existing = GetCollections(accountId);
        int added = 0, updated = 0, removed = 0;
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var inUse = new HashSet<(string, string)>();
            using (var cmd = Command(_connection, tx, "SELECT colour, shape FROM collections;"))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    inUse.Add((r.GetString(0), r.GetString(1)));
                }
            }

            foreach (var f in found)
            {
                var match = existing.FirstOrDefault(c => c.RemoteRef == f.RemoteRef && c.Kind == f.Kind);
                if (match is null)
                {
                    var (colour, shape) = CollectionStyle.Next(inUse.Count, inUse);
                    inUse.Add((colour, shape));
                    var server = CalendarColours.Snap(f.ServerColour);
                    InsertCollection(tx, new Collection(Guid.NewGuid().ToString("N"), accountId, f.Kind, f.Name, server ?? colour, shape,
                        true, f.ReadOnly, f.RemoteRef, f.SourceUrl, ServerColour: server));
                    added++;
                }
                else
                {
                    // A server colour is remembered; it sets the colour unless the user chose one (ui-colour-theme interim).
                    var server = CalendarColours.Snap(f.ServerColour) ?? match.ServerColour;
                    var colour = match.ColourOverride ? match.Colour : server ?? match.Colour;
                    if (match.Name != f.Name || match.ReadOnly != f.ReadOnly || match.SourceUrl != f.SourceUrl
                        || colour != match.Colour || server != match.ServerColour)
                    {
                        Execute(_connection, tx,
                            """
                            UPDATE collections SET name = $name, read_only = $ro, source_url = $source, colour = $colour,
                                server_colour = $server WHERE id = $id;
                            """,
                            ("$name", f.Name), ("$ro", f.ReadOnly), ("$source", f.SourceUrl), ("$colour", colour),
                            ("$server", server), ("$id", match.Id));
                        if (match.Name != f.Name)
                        {
                            Execute(_connection, tx,
                                "UPDATE search_index SET calendar = $name WHERE rowid IN (SELECT rowid FROM items WHERE collection_id = $id);",
                                ("$name", f.Name), ("$id", match.Id));
                        }
                        updated++;
                    }
                }
            }

            if (found.Count > 0)
            {
                foreach (var gone in existing.Where(c => !found.Any(f => f.RemoteRef == c.RemoteRef && f.Kind == c.Kind)))
                {
                    Execute(_connection, tx,
                        """
                        INSERT INTO conflicts (item_id, collection_id, kind, local_payload, server_payload, detected_at)
                        SELECT i.id, i.collection_id, i.kind, CASE WHEN i.deleted = 1 THEN NULL ELSE i.raw_payload END, NULL, $now
                        FROM items i WHERE i.collection_id = $c AND EXISTS (SELECT 1 FROM outbox o WHERE o.item_id = i.id);
                        DELETE FROM outbox WHERE item_id IN (SELECT id FROM items WHERE collection_id = $c);
                        DELETE FROM meta WHERE key = 'hidden:' || $c;
                        DELETE FROM collections WHERE id = $c;
                        """,
                        ("$c", gone.Id), ("$now", _time.GetUtcNow().ToUnixTimeMilliseconds()));
                    removed++;
                }
                ReassignDefaults(tx);
            }
            tx.Commit();
        }
        return new CollectionChanges(added, updated, removed);
    }

    /// <summary>A default calendar or task list that no longer exists moves to the first writable one, in sidebar order.</summary>
    private void ReassignDefaults(SqliteTransaction tx)
    {
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
    }

    // ---- Items: local writes (R5) ----

    public void CreateItem(Item item, string? groupId = null)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            Execute(_connection, tx, InsertItemSql, ItemParameters(item));
            RebuildItemOccurrences(tx, item.Id, OccurrenceWindow());
            IndexItem(_connection, tx, item.Id);
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
            IndexItem(_connection, tx, item.Id);
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

    /// <summary>
    /// Moves an item to another collection of the same account (calendar-views R8). An item whose only change is an
    /// unsent create is re-targeted in place. Otherwise the moved copy (with its own id) is queued as a create, then
    /// the original as a delete, in one linked group, so the delete waits for the create. Returns the moved item's id.
    /// </summary>
    public string MoveItem(string itemId, Item moved)
    {
        string id;
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var entries = new List<(long Id, OutboxOperation Op, bool InFlight)>();
            using (var cmd = Command(_connection, tx, "SELECT id, operation, in_flight FROM outbox WHERE item_id = $id ORDER BY id;", ("$id", itemId)))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    entries.Add((r.GetInt64(0), Enum.Parse<OutboxOperation>(r.GetString(1)), r.GetBoolean(2)));
                }
            }

            if (entries is [{ Op: OutboxOperation.Create, InFlight: false } create])
            {
                id = itemId;
                Execute(_connection, tx, UpdateItemSql, ItemParameters(moved with { Id = id }));
                Execute(_connection, tx, "UPDATE outbox SET payload = $p WHERE id = $id;", ("$p", moved.RawPayload), ("$id", create.Id));
            }
            else
            {
                id = moved.Id;
                var group = Guid.NewGuid().ToString("N");
                var baseEtag = GetLiveEtag(tx, itemId);
                Execute(_connection, tx, InsertItemSql, ItemParameters(moved));
                Enqueue(tx, id, OutboxOperation.Create, moved.RawPayload, null, group);
                Execute(_connection, tx,
                    "UPDATE items SET deleted = 1 WHERE id = $id; DELETE FROM occurrences WHERE item_id = $id;", ("$id", itemId));
                Enqueue(tx, itemId, OutboxOperation.Delete, null, baseEtag, group);
            }
            RebuildItemOccurrences(tx, id, OccurrenceWindow());
            IndexItem(_connection, tx, id);
            tx.Commit();
        }
        LocalChanged?.Invoke();
        return id;
    }

    /// <summary>
    /// A "this and following" split (recurring-events R6): the shortened original as an update, then the new series
    /// as a create, in one linked group, so the new series is only sent once the original is.
    /// </summary>
    public void SplitItem(Item original, Item following)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var group = Guid.NewGuid().ToString("N");
            var window = OccurrenceWindow();
            var baseEtag = GetLiveEtag(tx, original.Id);
            Execute(_connection, tx, UpdateItemSql, ItemParameters(original));
            RebuildItemOccurrences(tx, original.Id, window);
            IndexItem(_connection, tx, original.Id);
            Enqueue(tx, original.Id, OutboxOperation.Update, original.RawPayload, baseEtag, group);
            Execute(_connection, tx, InsertItemSql, ItemParameters(following));
            RebuildItemOccurrences(tx, following.Id, window);
            IndexItem(_connection, tx, following.Id);
            Enqueue(tx, following.Id, OutboxOperation.Create, following.RawPayload, null, group);
            tx.Commit();
        }
        LocalChanged?.Invoke();
    }

    /// <summary>Undo of a split (undo R5): the original put back, then the new series deleted, in one linked group.</summary>
    public void UndoSplit(Item restored, string followingId)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var group = Guid.NewGuid().ToString("N");
            var baseEtag = GetLiveEtag(tx, restored.Id);
            Execute(_connection, tx, UpdateItemSql, ItemParameters(restored));
            RebuildItemOccurrences(tx, restored.Id, OccurrenceWindow());
            IndexItem(_connection, tx, restored.Id);
            Enqueue(tx, restored.Id, OutboxOperation.Update, restored.RawPayload, baseEtag, group);
            var followingEtag = GetLiveEtag(tx, followingId);
            Execute(_connection, tx,
                "UPDATE items SET deleted = 1 WHERE id = $id; DELETE FROM occurrences WHERE item_id = $id;", ("$id", followingId));
            Enqueue(tx, followingId, OutboxOperation.Delete, null, followingEtag, group);
            tx.Commit();
        }
        LocalChanged?.Invoke();
    }

    /// <summary>The live item with this server id in the collection, or null.</summary>
    public Item? FindItemByRemoteId(string collectionId, string remoteId)
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null,
                $"SELECT {ItemColumns} FROM items i WHERE i.collection_id = $c AND i.remote_id = $remote AND i.deleted = 0;",
                ("$c", collectionId), ("$remote", remoteId));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadItem(r) : null;
        }
    }

    /// <summary>The live occurrences and exceptions of a server-expanded series (Graph), by the master's server id.</summary>
    public IReadOnlyList<Item> GetSeriesItems(string collectionId, string seriesId)
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null,
                $"SELECT {ItemColumns} FROM items i WHERE i.collection_id = $c AND i.series_id = $s AND i.deleted = 0 ORDER BY i.start_utc;",
                ("$c", collectionId), ("$s", seriesId));
            using var r = cmd.ExecuteReader();
            var items = new List<Item>();
            while (r.Read())
            {
                items.Add(ReadItem(r));
            }
            return items;
        }
    }

    /// <summary>
    /// Server ids of series (Graph seriesMasterId) whose live occurrences are stored without their master, in one
    /// collection. A master waiting to be deleted still counts as stored.
    /// </summary>
    public IReadOnlyList<string> GetOrphanSeriesIds(string collectionId)
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null,
                """
                SELECT DISTINCT i.series_id FROM items i
                WHERE i.collection_id = $c AND i.series_id IS NOT NULL AND i.deleted = 0
                  AND NOT EXISTS (SELECT 1 FROM items m WHERE m.collection_id = i.collection_id AND m.remote_id = i.series_id);
                """,
                ("$c", collectionId));
            using var r = cmd.ExecuteReader();
            var ids = new List<string>();
            while (r.Read())
            {
                ids.Add(r.GetString(0));
            }
            return ids;
        }
    }

    /// <summary>Returns the item, or null if it does not exist or is deleted (unless <paramref name="includeDeleted"/>).</summary>
    public Item? GetItem(string itemId, bool includeDeleted = false)
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null,
                $"SELECT {ItemColumns} FROM items i WHERE i.id = $id AND (i.deleted = 0 OR $all);", ("$id", itemId), ("$all", includeDeleted));
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
    /// Occurrences overlapping [from, to) in collections that are not hidden, with the fields the views draw,
    /// ordered by start (calendar-views R12: one query per render). An MS365 occurrence without its own show-as
    /// takes its series' (event-show-as).
    /// </summary>
    public IReadOnlyList<OccurrenceView> GetOccurrenceViews(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null,
                """
                SELECT o.item_id, i.collection_id, i.kind, COALESCE(i.summary, m.summary), COALESCE(i.location, m.location),
                       o.start_utc, o.end_utc, i.all_day, i.is_recurring, o.recurrence_utc, i.read_only OR COALESCE(m.read_only, 0),
                       CASE WHEN i.series_id IS NOT NULL AND i.summary IS NULL THEN COALESCE(m.unaccepted, 0) ELSE i.unaccepted END,
                       i.completed_utc IS NOT NULL OR lower(COALESCE(i.status, '')) = 'completed',
                       CASE WHEN i.kind = 'Event' THEN COALESCE(i.status, m.status) END,
                       COALESCE(i.join_url, m.join_url)
                FROM occurrences o JOIN items i ON i.id = o.item_id
                LEFT JOIN items m ON i.series_id IS NOT NULL AND m.collection_id = i.collection_id AND m.remote_id = i.series_id AND m.deleted = 0
                WHERE i.deleted = 0 AND o.start_utc < $to AND o.end_utc >= $from
                  AND NOT EXISTS (SELECT 1 FROM meta WHERE key = 'hidden:' || i.collection_id)
                ORDER BY o.start_utc, o.item_id;
                """,
                ("$from", from.ToUnixTimeMilliseconds()), ("$to", to.ToUnixTimeMilliseconds()));
            using var r = cmd.ExecuteReader();
            var list = new List<OccurrenceView>();
            while (r.Read())
            {
                list.Add(new OccurrenceView(r.GetString(0), r.GetString(1), Enum.Parse<ItemKind>(r.GetString(2)),
                    NullableString(r, 3), NullableString(r, 4), Time(r, 5), Time(r, 6), r.GetBoolean(7), r.GetBoolean(8),
                    r.IsDBNull(9) ? null : Time(r, 9), r.GetBoolean(10), r.GetBoolean(11), r.GetBoolean(12),
                    ShowAsValues.FromGraph(NullableString(r, 13)), NullableString(r, 14)));
            }
            return list;
        }
    }

    /// <summary>Live tasks in task lists that are not hidden, dated or not (tasks interim: the Tasks view).</summary>
    public IReadOnlyList<Item> GetTasks()
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null,
                $"""
                SELECT {ItemColumns} FROM items i
                WHERE i.deleted = 0 AND i.kind = 'Task'
                  AND NOT EXISTS (SELECT 1 FROM meta WHERE key = 'hidden:' || i.collection_id);
                """);
            using var r = cmd.ExecuteReader();
            var items = new List<Item>();
            while (r.Read())
            {
                items.Add(ReadItem(r));
            }
            return items;
        }
    }

    // ---- Collection colour and marker overrides (ui-colour-theme interim); local only ----

    /// <summary>A colour the user chose: later server colours no longer change it.</summary>
    // ---- Search ----

    /// <summary>
    /// Live events and tasks matching every typed word as a prefix, in collections that are not hidden (search R1, R2, R6).
    /// A series is one result (R7): items of one MS365 series, or one CalDAV item, are grouped and shown by the next
    /// matched occurrence, else the last one. Upcoming results come first, soonest first, then past ones, newest first,
    /// then undated tasks (R3). Titles and snippets mark matches with <see cref="SearchResult.MarkStart"/> and
    /// <see cref="SearchResult.MarkEnd"/>.
    /// </summary>
    public IReadOnlyList<SearchResult> Search(string text, int limit = 50)
    {
        if (SearchText.Query(text) is not { } query)
        {
            return [];
        }
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            // One row per matched item and occurrence; each group keeps its best row as it is read (R8).
            var groups = new Dictionary<string, (SearchResult Row, bool Indexed)>();
            using (var cmd = Command(_connection, null,
                """
                SELECT i.id, i.collection_id, i.kind, i.collection_id || '|' || COALESCE(i.series_id, i.remote_id, i.id), i.is_recurring,
                       i.all_day, i.completed_utc IS NOT NULL OR lower(COALESCE(i.status, '')) = 'completed',
                       COALESCE(o.start_utc, i.start_utc, i.due_utc), COALESCE(o.end_utc, i.end_utc, i.due_utc), o.recurrence_utc,
                       o.item_id IS NOT NULL
                FROM search_index s JOIN items i ON i.rowid = s.rowid LEFT JOIN occurrences o ON o.item_id = i.id
                WHERE search_index MATCH $q AND i.deleted = 0
                  AND i.collection_id NOT IN (SELECT substr(key, 8) FROM meta WHERE key LIKE 'hidden:%');
                """, ("$q", query)))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    var group = r.GetString(3);
                    var row = new SearchResult(r.GetString(0), r.GetString(1), Enum.Parse<ItemKind>(r.GetString(2)), "", null,
                        NullableTime(r, 7), NullableTime(r, 8), r.GetBoolean(5), r.IsDBNull(9) ? null : Time(r, 9), r.GetBoolean(4),
                        r.GetBoolean(6));
                    var indexed = r.GetBoolean(10);
                    if (!groups.TryGetValue(group, out var best))
                    {
                        groups[group] = (row, indexed);
                    }
                    else if (indexed && (!best.Indexed || Before(row, best.Row)))
                    {
                        groups[group] = (row with { IsSeries = row.IsSeries || best.Row.IsSeries }, true);
                    }
                    else if (row.IsSeries && !best.Row.IsSeries)
                    {
                        groups[group] = (best.Row with { IsSeries = true }, best.Indexed);
                    }
                }
            }
            var shown = groups.Values.Select(g => g.Row)
                .OrderBy(r => r.StartUtc is null ? 2 : Upcoming(r) ? 0 : 1)
                .ThenBy(r => r.StartUtc is not { } t ? 0 : Upcoming(r) ? t.UtcTicks : -t.UtcTicks)
                .Take(limit)
                .ToList();

            // Highlights only for the results shown (R8).
            var marks = new Dictionary<string, (string Title, string? Snippet)>();
            if (shown.Count > 0)
            {
                var ids = shown.Select(r => r.ItemId).ToList();
                using var cmd = Command(_connection, null,
                    $"""
                    SELECT i.id, highlight(search_index, 0, $a, $b), snippet(search_index, -1, $a, $b, '…', 12)
                    FROM search_index s JOIN items i ON i.rowid = s.rowid
                    WHERE search_index MATCH $q AND i.id IN ({InList(ids)});
                    """,
                    [("$q", query), ("$a", SearchResult.MarkStart.ToString()), ("$b", SearchResult.MarkEnd.ToString()), .. InParameters(ids)]);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    marks[r.GetString(0)] = (NullableString(r, 1) ?? "", NullableString(r, 2));
                }
            }
            return shown.Select(r =>
            {
                var (title, snippet) = marks.GetValueOrDefault(r.ItemId, ("", null));
                return r with { Title = title, Snippet = snippet is { } s && Unmarked(s) != Unmarked(title) ? s : null };
            }).ToList();
        }

        bool Upcoming(SearchResult r) => r.EndUtc > now;

        // An occurrence still to end beats a past one; the soonest upcoming, else the latest past one.
        bool Before(SearchResult a, SearchResult b) =>
            Upcoming(a) != Upcoming(b) ? Upcoming(a) : Upcoming(a) ? a.StartUtc < b.StartUtc : a.StartUtc > b.StartUtc;

        static string Unmarked(string s) => s.Replace(SearchResult.MarkStart.ToString(), "").Replace(SearchResult.MarkEnd.ToString(), "");
    }

    public void SetCollectionColour(string collectionId, string colour)
    {
        lock (_gate)
        {
            Execute(_connection, null, "UPDATE collections SET colour = $colour, colour_override = 1 WHERE id = $id;",
                ("$colour", colour), ("$id", collectionId));
        }
    }

    /// <summary>Back to the server colour, which sync follows again; nothing changes without one.</summary>
    public void UseServerColour(string collectionId)
    {
        lock (_gate)
        {
            Execute(_connection, null,
                "UPDATE collections SET colour = server_colour, colour_override = 0 WHERE id = $id AND server_colour IS NOT NULL;",
                ("$id", collectionId));
        }
    }

    /// <summary>A shape name or "Glyph:XXXX" for a Fluent icon (ui-colour-theme R7).</summary>
    public void SetCollectionShape(string collectionId, string shape)
    {
        lock (_gate)
        {
            Execute(_connection, null, "UPDATE collections SET shape = $shape WHERE id = $id;", ("$shape", shape), ("$id", collectionId));
        }
    }

    // ---- Working hours (calendar-shading R1; settings-page R5): local only ----

    private const string WorkingHoursKey = "setting:working-hours";

    public WorkingHours GetWorkingHours()
    {
        lock (_gate)
        {
            return WorkingHours.Parse(GetMeta(null, WorkingHoursKey));
        }
    }

    public void SetWorkingHours(WorkingHours hours)
    {
        if (hours.End <= hours.Start)
        {
            throw new ArgumentException("Working hours must end after they start.", nameof(hours));
        }
        lock (_gate)
        {
            SetMeta(null, WorkingHoursKey, hours.Serialize());
        }
    }

    // ---- Public holiday calendar (calendar-shading R3, R5): one at a time, local only ----

    private const string HolidayKey = "holiday-calendar";
    private const string HolidayEventsKey = "holiday-show-events";

    /// <summary>The holiday calendar's id; null when none is marked or it no longer exists.</summary>
    public string? GetHolidayCollectionId()
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null,
                "SELECT id FROM collections WHERE id = (SELECT value FROM meta WHERE key = $key);", ("$key", HolidayKey));
            return cmd.ExecuteScalar() as string;
        }
    }

    /// <summary>Marks <paramref name="collectionId"/> as the holiday calendar, replacing any other; null clears it.</summary>
    public void SetHolidayCollection(string? collectionId)
    {
        lock (_gate)
        {
            if (collectionId is null)
            {
                Execute(_connection, null, "DELETE FROM meta WHERE key = $key;", ("$key", HolidayKey));
            }
            else
            {
                SetMeta(null, HolidayKey, collectionId);
            }
        }
    }

    /// <summary>"Show as shading only" (R5); on unless turned off.</summary>
    public bool GetHolidayShadingOnly()
    {
        lock (_gate)
        {
            return GetMeta(null, HolidayEventsKey) is null;
        }
    }

    public void SetHolidayShadingOnly(bool on)
    {
        lock (_gate)
        {
            if (on)
            {
                Execute(_connection, null, "DELETE FROM meta WHERE key = $key;", ("$key", HolidayEventsKey));
            }
            else
            {
                SetMeta(null, HolidayEventsKey, "1");
            }
        }
    }

    // ---- Collection visibility (calendar-views R2; interim until calendar-sets) ----

    public IReadOnlySet<string> GetHiddenCollectionIds()
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null, "SELECT substr(key, 8) FROM meta WHERE key LIKE 'hidden:%';");
            using var r = cmd.ExecuteReader();
            var ids = new HashSet<string>();
            while (r.Read())
            {
                ids.Add(r.GetString(0));
            }
            return ids;
        }
    }

    public void SetCollectionHidden(string collectionId, bool hidden)
    {
        lock (_gate)
        {
            if (hidden)
            {
                SetMeta(null, "hidden:" + collectionId, "1");
            }
            else
            {
                Execute(_connection, null, "DELETE FROM meta WHERE key = $key;", ("$key", "hidden:" + collectionId));
            }
        }
    }

    // ---- Sidebar layout (calendar-views R2b): local UI state in meta under "ui:" ----

    /// <summary>A UI flag (e.g. sidebar hidden, a section collapsed); false when never set.</summary>
    public bool GetUiFlag(string name)
    {
        lock (_gate)
        {
            return GetMeta(null, "ui:" + name) is not null;
        }
    }

    public void SetUiFlag(string name, bool on)
    {
        lock (_gate)
        {
            if (on)
            {
                SetMeta(null, "ui:" + name, "1");
            }
            else
            {
                Execute(_connection, null, "DELETE FROM meta WHERE key = $key;", ("$key", "ui:" + name));
            }
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
    /// Removes a sent create or update and records the etag the server returned (null if it sent none, so the
    /// next pull fetches the item again). Later queued entries for the item are rebased onto that etag, so they
    /// do not fail with a false 412 (sync-radicale R6). A <paramref name="remoteId"/> is the id the server gave a
    /// created item (Graph).
    /// </summary>
    public void CompleteOutboxWrite(long entryId, string? etag, string? remoteId = null)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            if (remoteId is not null)
            {
                Execute(_connection, tx, "UPDATE items SET remote_id = $remote WHERE id = (SELECT item_id FROM outbox WHERE id = $id);",
                    ("$remote", remoteId), ("$id", entryId));
            }
            Execute(_connection, tx,
                """
                UPDATE items SET etag = $etag WHERE id = (SELECT item_id FROM outbox WHERE id = $id);
                UPDATE outbox SET base_etag = $etag WHERE id > $id AND item_id = (SELECT item_id FROM outbox WHERE id = $id);
                DELETE FROM outbox WHERE id = $id;
                """,
                ("$id", entryId), ("$etag", etag));
            tx.Commit();
        }
    }

    /// <summary>
    /// A write the server refused although its version is the echo of the app's own write (conflict-resolution R11):
    /// the item and its queued entries from this one on take the server's etag, and this entry is due again.
    /// </summary>
    public void RebaseOutboxEntry(long entryId, string? etag)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            Execute(_connection, tx,
                """
                UPDATE items SET etag = $etag WHERE id = (SELECT item_id FROM outbox WHERE id = $id);
                UPDATE outbox SET base_etag = $etag WHERE id >= $id AND item_id = (SELECT item_id FROM outbox WHERE id = $id);
                UPDATE outbox SET in_flight = 0 WHERE id = $id;
                """,
                ("$id", entryId), ("$etag", etag));
            tx.Commit();
        }
    }

    /// <summary>
    /// A write the server refused with 412 (conflict-resolution R1-R4): the server version wins. The local version
    /// is kept in Conflicts, and every queued entry for the item is dropped. A null <paramref name="server"/>
    /// means the server no longer has the item, so it is removed locally.
    /// </summary>
    public void ResolveConflict(long entryId, Item? server)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            string? itemId = null, group = null;
            using (var cmd = Command(_connection, tx, "SELECT item_id, group_id FROM outbox WHERE id = $id;", ("$id", entryId)))
            using (var r = cmd.ExecuteReader())
            {
                if (r.Read())
                {
                    (itemId, group) = (r.GetString(0), NullableString(r, 1));
                }
            }
            if (itemId is not null)
            {
                TakeServerVersion(tx, itemId, server);
                if (group is not null)
                {
                    CancelGroup(tx, group);
                }
            }
            tx.Commit();
        }
    }

    /// <summary>Number of conflict copies kept (conflict-resolution R5, R7).</summary>
    public int GetConflictCount()
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null, "SELECT COUNT(*) FROM conflicts;");
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>The kept conflict copies, newest first, for the conflicts list (conflict-resolution R5 interim).</summary>
    public IReadOnlyList<ConflictInfo> GetConflicts()
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null,
                """
                SELECT c.id, c.item_id, c.collection_id, c.kind, c.local_payload, c.detected_at,
                       i.id IS NOT NULL AND i.deleted = 0, i.summary, i.start_utc, i.all_day
                FROM conflicts c LEFT JOIN items i ON i.id = c.item_id
                ORDER BY c.detected_at DESC, c.id DESC;
                """);
            using var r = cmd.ExecuteReader();
            var list = new List<ConflictInfo>();
            while (r.Read())
            {
                var local = NullableString(r, 4);
                var onCalendar = r.GetBoolean(6);
                var title = onCalendar ? NullableString(r, 7) : PayloadTitle(local);
                list.Add(new ConflictInfo(r.GetInt64(0), r.GetString(1), r.GetString(2), Enum.Parse<ItemKind>(r.GetString(3)),
                    string.IsNullOrWhiteSpace(title) ? "(No title)" : title, onCalendar ? NullableTime(r, 8) : null,
                    onCalendar && r.GetBoolean(9), local is null, onCalendar, Time(r, 5)));
            }
            return list;
        }
    }

    /// <summary>One kept conflict copy with its saved local version, or null when it is gone (conflict-resolution R6).</summary>
    public ConflictRecord? GetConflict(long id)
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null,
                "SELECT item_id, collection_id, kind, local_payload FROM conflicts WHERE id = $id;", ("$id", id));
            using var r = cmd.ExecuteReader();
            return r.Read()
                ? new ConflictRecord(id, r.GetString(0), r.GetString(1), Enum.Parse<ItemKind>(r.GetString(2)), NullableString(r, 3))
                : null;
        }
    }

    /// <summary>Removes a kept conflict copy once the user has resolved it (conflict-resolution R6).</summary>
    public void DeleteConflict(long id)
    {
        lock (_gate)
        {
            Execute(_connection, null, "DELETE FROM conflicts WHERE id = $id;", ("$id", id));
        }
    }

    /// <summary>
    /// The title in a raw payload: Graph JSON "subject" (events) or "title" (tasks), else the iCalendar SUMMARY.
    /// Null when there is none.
    /// </summary>
    internal static string? PayloadTitle(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }
        if (payload.TrimStart().StartsWith('{'))
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(payload);
                foreach (var name in new[] { "subject", "title" })
                {
                    if (json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        return value.GetString();
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
            return null;
        }
        // Unfold continuation lines (RFC 5545 3.1), then take the first SUMMARY, with or without parameters.
        var unfolded = payload.Replace("\r\n ", "").Replace("\r\n\t", "").Replace("\n ", "").Replace("\n\t", "");
        foreach (var line in unfolded.Split('\n'))
        {
            if (line.StartsWith("SUMMARY", StringComparison.OrdinalIgnoreCase) && line.Length > 7 && line[7] is ':' or ';'
                && line.IndexOf(':') is var colon and > 0)
            {
                return line[(colon + 1)..].TrimEnd('\r')
                    .Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
            }
        }
        return null;
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
    /// If the item has unsent local changes, the server version wins and the local one goes to Conflicts
    /// (conflict-resolution R1, R2, R4); an entry being sent right now keeps the local version until the next sync.
    /// </summary>
    public ServerChangeResult ApplyServerItem(IReadOnlyList<string> collectionIds, Item item)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var existingId = FindByRemoteId(tx, collectionIds, item.RemoteId!);
            if (existingId is not null && Pending(tx, existingId) is { } pending)
            {
                if (pending.InFlight)
                {
                    return ServerChangeResult.KeptLocal;
                }
                TakeServerVersion(tx, existingId, item);
                tx.Commit();
                return ServerChangeResult.Conflict;
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
            IndexItem(_connection, tx, item.Id);
            tx.Commit();
            return ServerChangeResult.Applied;
        }
    }

    /// <summary>
    /// Deletes the item the server removed. An item never sent (pending create) or being sent is kept. A local
    /// edit is kept in Conflicts (conflict-resolution R3); a local delete simply agrees with the server.
    /// </summary>
    public ServerChangeResult RemoveServerItem(IReadOnlyList<string> collectionIds, string remoteId)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var id = FindByRemoteId(tx, collectionIds, remoteId);
            if (id is null)
            {
                return ServerChangeResult.Applied;
            }
            if (Pending(tx, id) is { } pending)
            {
                if (pending.InFlight || pending.HasCreate)
                {
                    return ServerChangeResult.KeptLocal;
                }
                if (!pending.LocalDelete)
                {
                    TakeServerVersion(tx, id, null);
                    tx.Commit();
                    return ServerChangeResult.Conflict;
                }
            }
            Execute(_connection, tx, "DELETE FROM outbox WHERE item_id = $id; DELETE FROM items WHERE id = $id;", ("$id", id));
            tx.Commit();
            return ServerChangeResult.Applied;
        }
    }

    // ---- Internals ----

    private const string InsertItemSql =
        """
        INSERT INTO items (id, collection_id, kind, remote_id, raw_payload, summary, start_utc, end_utc, all_day,
            time_zone, location, status, due_utc, completed_utc, is_recurring, etag, last_modified, series_id, read_only, unaccepted, join_url)
        VALUES ($id, $collection, $kind, $remote, $raw, $summary, $start, $end, $allDay,
            $tz, $location, $status, $due, $completed, $recurring, $etag, $modified, $series, $readOnly, $unaccepted, $join);
        """;

    private const string UpdateItemSql =
        """
        UPDATE items SET collection_id = $collection, kind = $kind, remote_id = $remote, raw_payload = $raw,
            summary = $summary, start_utc = $start, end_utc = $end, all_day = $allDay, time_zone = $tz,
            location = $location, status = $status, due_utc = $due, completed_utc = $completed,
            is_recurring = $recurring, etag = $etag, last_modified = $modified, series_id = $series, read_only = $readOnly, unaccepted = $unaccepted,
            join_url = $join
        WHERE id = $id;
        """;

    private string? FindByRemoteId(SqliteTransaction tx, IReadOnlyList<string> collectionIds, string remoteId)
    {
        using var cmd = Command(_connection, tx,
            $"SELECT id FROM items WHERE remote_id = $remote AND collection_id IN ({InList(collectionIds)});",
            [("$remote", remoteId), .. InParameters(collectionIds)]);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>The item's queued entries, summarised; null if it has none.</summary>
    private (bool InFlight, bool HasCreate, bool LocalDelete)? Pending(SqliteTransaction tx, string itemId)
    {
        using var cmd = Command(_connection, tx,
            """
            SELECT MAX(in_flight), MAX(operation = 'Create'), (SELECT deleted FROM items WHERE id = $id)
            FROM outbox WHERE item_id = $id HAVING COUNT(*) > 0;
            """,
            ("$id", itemId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetBoolean(0), r.GetBoolean(1), r.GetBoolean(2)) : null;
    }

    /// <summary>
    /// Keeps the local version in Conflicts (null payload: it was a local delete), drops the item's queued entries,
    /// and replaces the item with the server version, or removes it when the server has none (conflict-resolution R2).
    /// </summary>
    private void TakeServerVersion(SqliteTransaction tx, string itemId, Item? server)
    {
        string collectionId;
        using (var cmd = Command(_connection, tx, "SELECT collection_id FROM items WHERE id = $id;", ("$id", itemId)))
        {
            collectionId = (string)cmd.ExecuteScalar()!;
        }
        Execute(_connection, tx,
            """
            INSERT INTO conflicts (item_id, collection_id, kind, local_payload, server_payload, detected_at)
            SELECT id, collection_id, kind, CASE WHEN deleted = 1 THEN NULL ELSE raw_payload END, $server, $now
            FROM items WHERE id = $id;
            DELETE FROM outbox WHERE item_id = $id;
            """,
            ("$id", itemId), ("$server", server?.RawPayload), ("$now", _time.GetUtcNow().ToUnixTimeMilliseconds()));
        if (server is null)
        {
            Execute(_connection, tx, "DELETE FROM items WHERE id = $id;", ("$id", itemId));
            return;
        }
        Execute(_connection, tx, UpdateItemSql + " UPDATE items SET deleted = 0 WHERE id = $id;",
            ItemParameters(server with { Id = itemId, CollectionId = collectionId }));
        RebuildItemOccurrences(tx, itemId, OccurrenceWindow());
        IndexItem(_connection, tx, itemId);
    }

    /// <summary>
    /// After a conflict in a linked group, its later entries are not sent (conflict-resolution interim): a create's
    /// item goes to Conflicts and leaves the calendar; a deleted item comes back.
    /// </summary>
    private void CancelGroup(SqliteTransaction tx, string group)
    {
        var entries = new List<(long Id, string ItemId, OutboxOperation Op)>();
        using (var cmd = Command(_connection, tx, "SELECT id, item_id, operation FROM outbox WHERE group_id = $g ORDER BY id;", ("$g", group)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                entries.Add((r.GetInt64(0), r.GetString(1), Enum.Parse<OutboxOperation>(r.GetString(2))));
            }
        }
        foreach (var (id, itemId, op) in entries)
        {
            if (op == OutboxOperation.Create)
            {
                Execute(_connection, tx,
                    """
                    INSERT INTO conflicts (item_id, collection_id, kind, local_payload, server_payload, detected_at)
                    SELECT id, collection_id, kind, raw_payload, NULL, $now FROM items WHERE id = $id;
                    DELETE FROM outbox WHERE item_id = $id;
                    DELETE FROM occurrences WHERE item_id = $id;
                    DELETE FROM items WHERE id = $id;
                    """,
                    ("$id", itemId), ("$now", _time.GetUtcNow().ToUnixTimeMilliseconds()));
            }
            else if (op == OutboxOperation.Delete)
            {
                Execute(_connection, tx, "UPDATE items SET deleted = 0 WHERE id = $item; DELETE FROM outbox WHERE id = $id;",
                    ("$item", itemId), ("$id", id));
                RebuildItemOccurrences(tx, itemId, OccurrenceWindow());
            }
        }
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
            INSERT INTO collections (id, account_id, kind, name, colour, shape, reminders_on, read_only, remote_ref, source_url,
                colour_override, server_colour)
            VALUES ($id, $account, $kind, $name, $colour, $shape, $reminders, $readOnly, $remote, $source, $override, $server);
            """,
            ("$id", c.Id), ("$account", c.AccountId), ("$kind", c.Kind.ToString()), ("$name", c.Name),
            ("$colour", c.Colour), ("$shape", c.Shape), ("$reminders", c.RemindersOn), ("$readOnly", c.ReadOnly),
            ("$remote", c.RemoteRef), ("$source", c.SourceUrl), ("$override", c.ColourOverride), ("$server", c.ServerColour));

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

    /// <summary>Replaces one item's row in the search index (search R1), in the transaction that wrote it.</summary>
    private static void IndexItem(SqliteConnection connection, SqliteTransaction tx, string itemId)
    {
        long rowId;
        string? title, location, calendar;
        string payload;
        using (var cmd = Command(connection, tx,
            "SELECT i.rowid, i.summary, i.location, i.raw_payload, c.name FROM items i JOIN collections c ON c.id = i.collection_id WHERE i.id = $id;",
            ("$id", itemId)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read())
            {
                return;
            }
            (rowId, title, location, payload, calendar) = (r.GetInt64(0), NullableString(r, 1), NullableString(r, 2), r.GetString(3), r.GetString(4));
        }
        Execute(connection, tx,
            """
            DELETE FROM search_index WHERE rowid = $row;
            INSERT INTO search_index (rowid, title, body, location, calendar) VALUES ($row, $title, $body, $location, $calendar);
            """,
            ("$row", rowId), ("$title", title), ("$body", SearchText.Body(payload)), ("$location", location), ("$calendar", calendar));
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
            accountType = Enum.Parse<AccountType>(r.GetString(21));
        }

        Execute(_connection, tx, "DELETE FROM occurrences WHERE item_id = $id;", ("$id", itemId));
        foreach (var (start, end, recurrence) in OccurrenceExpander.Expand(item, accountType, window.Start, window.End, window.Zone))
        {
            Execute(_connection, tx,
                "INSERT INTO occurrences (item_id, start_utc, end_utc, recurrence_utc) VALUES ($id, $s, $e, $r);",
                ("$id", itemId), ("$s", start.ToUnixTimeMilliseconds()), ("$e", end.ToUnixTimeMilliseconds()),
                ("$r", item.IsRecurring ? recurrence.ToUnixTimeMilliseconds() : null));
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
        NullableString(r, 15), NullableTime(r, 16), NullableString(r, 17), r.GetBoolean(18), r.GetBoolean(19), NullableString(r, 20));

    private static (string, object?)[] ItemParameters(Item i) =>
    [
        ("$id", i.Id), ("$collection", i.CollectionId), ("$kind", i.Kind.ToString()), ("$remote", i.RemoteId),
        ("$raw", i.RawPayload), ("$summary", i.Summary), ("$start", i.StartUtc?.ToUnixTimeMilliseconds()),
        ("$end", i.EndUtc?.ToUnixTimeMilliseconds()), ("$allDay", i.AllDay), ("$tz", i.TimeZone),
        ("$location", i.Location), ("$status", i.Status), ("$due", i.DueUtc?.ToUnixTimeMilliseconds()),
        ("$completed", i.CompletedUtc?.ToUnixTimeMilliseconds()), ("$recurring", i.IsRecurring), ("$etag", i.Etag),
        ("$modified", i.LastModifiedUtc?.ToUnixTimeMilliseconds()), ("$series", i.SeriesId), ("$readOnly", i.ReadOnly), ("$unaccepted", i.Unaccepted),
        ("$join", i.JoinUrl),
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
