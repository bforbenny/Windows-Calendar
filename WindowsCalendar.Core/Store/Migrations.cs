using Microsoft.Data.Sqlite;

namespace WindowsCalendar.Core.Store;

/// <summary>The store was written by a newer build; it is not opened or changed (app-updates R4).</summary>
public sealed class StoreTooNewException(int storeVersion, int supportedVersion)
    : Exception($"Store schema version {storeVersion} is newer than supported version {supportedVersion}.")
{
    public int StoreVersion { get; } = storeVersion;
    public int SupportedVersion { get; } = supportedVersion;
}

/// <summary>
/// Forward-only schema migrations (local-store-offline R1). Index + 1 is the schema version.
/// Never edit a shipped migration; append a new one.
/// </summary>
internal static class Migrations
{
    private static readonly string[] Steps =
    [
        // v1: accounts, collections, items, outbox, sync state.
        """
        CREATE TABLE accounts (
            id           TEXT PRIMARY KEY,
            type         TEXT NOT NULL,
            display_name TEXT NOT NULL
        );

        CREATE TABLE collections (
            id           TEXT PRIMARY KEY,
            account_id   TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            kind         TEXT NOT NULL,
            name         TEXT NOT NULL,
            colour       TEXT NOT NULL,
            shape        TEXT NOT NULL,
            reminders_on INTEGER NOT NULL,
            read_only    INTEGER NOT NULL,
            remote_ref   TEXT NOT NULL
        );

        CREATE TABLE items (
            id             TEXT PRIMARY KEY,
            collection_id  TEXT NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
            kind           TEXT NOT NULL,
            remote_id      TEXT,
            raw_payload    TEXT NOT NULL,
            summary        TEXT,
            start_utc      INTEGER,
            end_utc        INTEGER,
            all_day        INTEGER NOT NULL,
            time_zone      TEXT,
            location       TEXT,
            status         TEXT,
            due_utc        INTEGER,
            completed_utc  INTEGER,
            is_recurring   INTEGER NOT NULL,
            etag           TEXT,
            last_modified  INTEGER,
            deleted        INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_items_collection_start ON items(collection_id, start_utc);
        CREATE INDEX ix_items_remote ON items(collection_id, remote_id);

        CREATE TABLE outbox (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            item_id     TEXT NOT NULL,
            operation   TEXT NOT NULL,
            payload     TEXT,
            base_etag   TEXT,
            created_at  INTEGER NOT NULL,
            attempts    INTEGER NOT NULL DEFAULT 0,
            last_error  TEXT,
            group_id    TEXT,
            in_flight   INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_outbox_item ON outbox(item_id);

        CREATE TABLE sync_state (
            collection_id TEXT NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
            key           TEXT NOT NULL,
            value         TEXT NOT NULL,
            PRIMARY KEY (collection_id, key)
        );
        """,

        // v2: occurrence index (R3), outbox backoff (R7), store-wide values.
        """
        CREATE TABLE occurrences (
            item_id   TEXT NOT NULL REFERENCES items(id) ON DELETE CASCADE,
            start_utc INTEGER NOT NULL,
            end_utc   INTEGER NOT NULL
        );
        CREATE INDEX ix_occurrences_start ON occurrences(start_utc);
        CREATE INDEX ix_occurrences_item ON occurrences(item_id);

        ALTER TABLE outbox ADD COLUMN next_attempt_at INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE outbox ADD COLUMN permanent INTEGER NOT NULL DEFAULT 0;

        CREATE TABLE meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """,

        // v3: CalDAV account details (account-management R4, R4a). Not secrets.
        """
        ALTER TABLE accounts ADD COLUMN server_url TEXT;
        ALTER TABLE accounts ADD COLUMN username TEXT;
        ALTER TABLE accounts ADD COLUMN email TEXT;
        """,

        // v4: conflict copies (conflict-resolution R2). No foreign keys: a copy outlives its item.
        """
        CREATE TABLE conflicts (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            item_id        TEXT NOT NULL,
            collection_id  TEXT NOT NULL,
            kind           TEXT NOT NULL,
            local_payload  TEXT,
            server_payload TEXT,
            detected_at    INTEGER NOT NULL
        );
        """,

        // v5: feed URL of a subscribed calendar (sync-radicale R1a).
        """
        ALTER TABLE collections ADD COLUMN source_url TEXT;
        """,

        // v6: each occurrence's original start (RECURRENCE-ID) for recurring items (recurring-events interim);
        // forces a full index rebuild to fill it.
        """
        ALTER TABLE occurrences ADD COLUMN recurrence_utc INTEGER;
        DELETE FROM meta WHERE key = 'occurrences_built';
        """,

        // v7: an occurrence's series master (Graph seriesMasterId) and events the app must not change (MS365
        // meetings), sync-ms365 second slice. The Graph chunk state is reset so every calendar is pulled again with
        // its series masters.
        """
        ALTER TABLE items ADD COLUMN series_id TEXT;
        ALTER TABLE items ADD COLUMN read_only INTEGER NOT NULL DEFAULT 0;
        DELETE FROM sync_state WHERE key = 'graph-chunks';
        """,

        // v8: invitations you have not accepted, drawn muted (attendees-invitations interim). The Graph chunk state is
        // reset again so every event is read with it.
        """
        ALTER TABLE items ADD COLUMN unaccepted INTEGER NOT NULL DEFAULT 0;
        DELETE FROM sync_state WHERE key = 'graph-chunks';
        """,

        // v9: tasks sit on their due date, not their start (tasks interim); forces a full index rebuild.
        """
        DELETE FROM meta WHERE key = 'occurrences_built';
        """,

        // v10: colour overrides and the snapped server colour (ui-colour-theme interim).
        """
        ALTER TABLE collections ADD COLUMN colour_override INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE collections ADD COLUMN server_colour TEXT;
        """,

        // v11: CalDAV events keep their show-as in the status column, as MS365 events do (event-show-as); the store
        // reads existing CalDAV events again once on open (LocalStore.RemapShowAs).
        """
        INSERT INTO meta (key, value) VALUES ('remap-show-as', '1');
        """,

        // v12: each event's online-meeting join link (online-meeting-join R3); existing events are read again once on
        // open (LocalStore.Remap).
        """
        ALTER TABLE items ADD COLUMN join_url TEXT;
        INSERT INTO meta (key, value) VALUES ('remap-join-url', '1');
        """,

        // v13: full-text search (search R1). A row's rowid is its item's rowid (the app never runs VACUUM, which could
        // renumber them); the store fills it from existing items once on open (LocalStore.BuildSearchIndex).
        """
        CREATE VIRTUAL TABLE search_index USING fts5(title, body, location, calendar,
            tokenize = 'unicode61 remove_diacritics 2', prefix = '2 3');
        CREATE TRIGGER items_search_delete AFTER DELETE ON items BEGIN
            DELETE FROM search_index WHERE rowid = old.rowid;
        END;
        INSERT INTO meta (key, value) VALUES ('build-search', '1');
        """,

        // v14: per-reminder local state (reminders-notifications R2, R4). One row per alarm of an occurrence, keyed by
        // Reminder.Key; 'scheduled' marks a reminder already handed to Windows, so it is never shown twice.
        """
        CREATE TABLE reminder_state (
            key            TEXT PRIMARY KEY,
            item_id        TEXT NOT NULL,
            occurrence_utc INTEGER,
            fire_utc       INTEGER NOT NULL,
            state          TEXT NOT NULL,
            snooze_until   INTEGER
        );
        CREATE INDEX ix_reminder_state_fire ON reminder_state(fire_utc);
        """,
    ];

    public static int LatestVersion => Steps.Length;

    /// <summary>Applies pending migrations, each in its own transaction.</summary>
    public static void Apply(SqliteConnection connection)
    {
        var current = GetVersion(connection);
        if (current > LatestVersion)
        {
            throw new StoreTooNewException(current, LatestVersion);
        }

        for (var version = current + 1; version <= LatestVersion; version++)
        {
            using var tx = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = Steps[version - 1];
                cmd.ExecuteNonQuery();
                // PRAGMA cannot take parameters; version is an int we control.
                cmd.CommandText = $"PRAGMA user_version = {version};";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private static int GetVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
