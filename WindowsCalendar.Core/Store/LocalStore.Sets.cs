using Microsoft.Data.Sqlite;

namespace WindowsCalendar.Core.Store;

/// <summary>
/// Calendar sets (calendar-sets R1, R2, R5, R8): named groups of collections, stored locally and never synced. The
/// sidebar ticks (the meta "hidden:" keys) stay the current visibility; switching a set rewrites them to its members.
/// "All" is built in, not a row: every collection, and no active set id in meta. Each set also holds the secondary
/// timezone ruler (R11): the current ruler is in meta like the ticks, and "All"'s default ruler too.
/// </summary>
public sealed partial class LocalStore
{
    private const string ActiveSetKey = "active_set";
    private const string RulerKey = "ruler";
    private const string DefaultRulerKey = "ruler_default";

    /// <summary>User-defined sets in selector order.</summary>
    public IReadOnlyList<CalendarSet> GetCalendarSets()
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null, "SELECT id, name, position FROM calendar_sets ORDER BY position, rowid;");
            using var r = cmd.ExecuteReader();
            var sets = new List<CalendarSet>();
            while (r.Read())
            {
                sets.Add(new CalendarSet(r.GetString(0), r.GetString(1), r.GetInt32(2)));
            }
            return sets;
        }
    }

    /// <summary>The active user-defined set's id; null for "All".</summary>
    public string? ActiveSetId
    {
        get { lock (_gate) { return ActiveSet(null); } }
    }

    /// <summary>"Save as new set" (R3): a set, last in order, holding the collections ticked now; it becomes the active set.</summary>
    public CalendarSet CreateCalendarSet(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("A set needs a name.", nameof(name));
        }
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            int position;
            using (var cmd = Command(_connection, tx, "SELECT COALESCE(MAX(position) + 1, 0) FROM calendar_sets;"))
            {
                position = Convert.ToInt32(cmd.ExecuteScalar());
            }
            var set = new CalendarSet(Guid.NewGuid().ToString("N"), name, position);
            Execute(_connection, tx, "INSERT INTO calendar_sets (id, name, position) VALUES ($id, $name, $pos);",
                ("$id", set.Id), ("$name", set.Name), ("$pos", set.Position));
            SaveMembers(tx, set.Id);
            SaveRuler(tx, set.Id);
            SetMeta(tx, ActiveSetKey, set.Id);
            tx.Commit();
            return set;
        }
    }

    /// <summary>"Update '&lt;set&gt;'" (R3, R11): the set's members become the collections ticked now, its ruler the current one.</summary>
    public void UpdateCalendarSet(string setId)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            SaveMembers(tx, setId);
            SaveRuler(tx, setId);
            tx.Commit();
        }
    }

    /// <summary>Settings &gt; Calendar sets (R2, R3): renames a set; the name is trimmed and must not be empty.</summary>
    public void RenameCalendarSet(string setId, string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("A set needs a name.", nameof(name));
        }
        lock (_gate)
        {
            Execute(_connection, null, "UPDATE calendar_sets SET name = $name WHERE id = $id;", ("$id", setId), ("$name", name));
        }
    }

    /// <summary>
    /// Settings &gt; Calendar sets (R2, R3): swaps a set with its neighbour, up (<paramref name="delta"/> -1) or down
    /// (+1); nothing at the ends. Positions are renumbered 0, 1, 2 … in the new order.
    /// </summary>
    public void MoveCalendarSet(string setId, int delta)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var ids = new List<string>();
            using (var cmd = Command(_connection, tx, "SELECT id FROM calendar_sets ORDER BY position, rowid;"))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    ids.Add(r.GetString(0));
                }
            }
            var from = ids.IndexOf(setId);
            var to = from + Math.Sign(delta);
            if (from < 0 || to < 0 || to >= ids.Count)
            {
                return;
            }
            (ids[from], ids[to]) = (ids[to], ids[from]);
            for (var i = 0; i < ids.Count; i++)
            {
                Execute(_connection, tx, "UPDATE calendar_sets SET position = $pos WHERE id = $id;", ("$id", ids[i]), ("$pos", i));
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Settings &gt; Calendar sets (R2, R3): deletes a set (its members go with it); deleting the active set switches
    /// to "All", which shows every collection.
    /// </summary>
    public void DeleteCalendarSet(string setId)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var wasActive = ActiveSet(tx) == setId;
            Execute(_connection, tx, "DELETE FROM calendar_sets WHERE id = $id;", ("$id", setId));
            if (wasActive)
            {
                Execute(_connection, tx, "DELETE FROM meta WHERE key LIKE 'hidden:%' OR key = $key;", ("$key", ActiveSetKey));
                SetRulerMeta(tx, RulerKey, ReadRuler(tx, DefaultRulerKey));
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Makes <paramref name="setId"/> (null: "All") the active set and rewrites the sidebar ticks to its members, which
    /// drops any manual override (R5); the current ruler becomes the set's, or the "All" default (R11).
    /// </summary>
    public void SwitchToSet(string? setId)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            Execute(_connection, tx, "DELETE FROM meta WHERE key LIKE 'hidden:%';");
            if (setId is null)
            {
                Execute(_connection, tx, "DELETE FROM meta WHERE key = $key;", ("$key", ActiveSetKey));
            }
            else
            {
                Execute(_connection, tx,
                    """
                    INSERT INTO meta (key, value)
                    SELECT 'hidden:' || id, '1' FROM collections
                    WHERE id NOT IN (SELECT collection_id FROM calendar_set_members WHERE set_id = $set);
                    """,
                    ("$set", setId));
                SetMeta(tx, ActiveSetKey, setId);
            }
            SetRulerMeta(tx, RulerKey, RulerOf(tx, setId));
            tx.Commit();
        }
    }

    /// <summary>
    /// True while the sidebar ticks differ from the active set's members, or the current ruler from its ruler, shown as
    /// "(modified)" (R5, R11). "All" is never modified by the ruler: a change there is its default.
    /// </summary>
    public bool IsActiveSetModified()
    {
        lock (_gate)
        {
            var active = ActiveSet(null);
            if (active is not null && !ReadRuler(null, RulerKey).SameAs(RulerOf(null, active)))
            {
                return true;
            }
            // A collection whose tick does not match: hidden though in the set, or shown though not ("All" has every one).
            using var cmd = Command(_connection, null,
                """
                SELECT EXISTS (
                    SELECT 1 FROM collections c
                    WHERE EXISTS (SELECT 1 FROM meta WHERE key = 'hidden:' || c.id)
                        = ($set IS NULL OR EXISTS (
                              SELECT 1 FROM calendar_set_members m WHERE m.set_id = $set AND m.collection_id = c.id)));
                """,
                ("$set", active));
            return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
        }
    }

    /// <summary>The secondary timezone ruler shown now (timezone-handling R8, calendar-sets R11).</summary>
    public RulerChoice GetRuler()
    {
        lock (_gate)
        {
            return ReadRuler(null, RulerKey);
        }
    }

    /// <summary>
    /// Changes the ruler shown now (R11). While "All" is active it is also saved as the default at once; while a
    /// user-defined set is active the set is "(modified)" until Update or the next switch.
    /// </summary>
    public void SetRuler(RulerChoice ruler)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            SetRulerMeta(tx, RulerKey, ruler);
            if (ActiveSet(tx) is null)
            {
                SetRulerMeta(tx, DefaultRulerKey, ruler);
            }
            tx.Commit();
        }
    }

    /// <summary>The active set's id, if it still exists.</summary>
    private string? ActiveSet(SqliteTransaction? tx)
    {
        using var cmd = Command(_connection, tx,
            "SELECT id FROM calendar_sets WHERE id = (SELECT value FROM meta WHERE key = $key);", ("$key", ActiveSetKey));
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>A set's ruler, or the "All" default for "All" (null) and for a set saved without one (R11).</summary>
    private RulerChoice RulerOf(SqliteTransaction? tx, string? setId)
    {
        if (setId is not null)
        {
            using var cmd = Command(_connection, tx, "SELECT ruler_on, ruler_zone FROM calendar_sets WHERE id = $id;", ("$id", setId));
            using var r = cmd.ExecuteReader();
            if (r.Read() && !r.IsDBNull(0))
            {
                return new RulerChoice(r.GetBoolean(0), r.IsDBNull(1) ? null : r.GetString(1));
            }
        }
        return ReadRuler(tx, DefaultRulerKey);
    }

    /// <summary>Saves the current ruler into a set (R11).</summary>
    private void SaveRuler(SqliteTransaction tx, string setId)
    {
        var ruler = ReadRuler(tx, RulerKey);
        Execute(_connection, tx, "UPDATE calendar_sets SET ruler_on = $on, ruler_zone = $zone WHERE id = $id;",
            ("$id", setId), ("$on", ruler.On), ("$zone", ruler.ZoneId));
    }

    /// <summary>A ruler in meta as "1|zone" or "0|zone" (the zone may be empty); off when absent.</summary>
    private RulerChoice ReadRuler(SqliteTransaction? tx, string key) =>
        GetMeta(tx, key) is { Length: >= 2 } v && v[1] == '|' ? new RulerChoice(v[0] == '1', v.Length > 2 ? v[2..] : null) : RulerChoice.Off;

    private void SetRulerMeta(SqliteTransaction tx, string key, RulerChoice ruler) =>
        SetMeta(tx, key, (ruler.On ? "1|" : "0|") + ruler.ZoneId);

    /// <summary>Replaces a set's members with the collections not hidden now.</summary>
    private void SaveMembers(SqliteTransaction tx, string setId) =>
        Execute(_connection, tx,
            """
            DELETE FROM calendar_set_members WHERE set_id = $set;
            INSERT INTO calendar_set_members (set_id, collection_id)
            SELECT $set, id FROM collections c WHERE NOT EXISTS (SELECT 1 FROM meta WHERE key = 'hidden:' || c.id);
            """,
            ("$set", setId));
}
