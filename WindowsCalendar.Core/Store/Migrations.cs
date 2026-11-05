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
