using WindowsCalendar.Core.Reminders;

namespace WindowsCalendar.Core.Store;

/// <summary>Per-reminder local state (reminders-notifications R2, R4); never synced.</summary>
public sealed partial class LocalStore
{
    /// <summary>Keys of reminders already scheduled or shown, so a late one is never shown twice.</summary>
    public IReadOnlySet<string> GetHandledReminderKeys()
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null, "SELECT key FROM reminder_state;");
            using var r = cmd.ExecuteReader();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (r.Read())
            {
                keys.Add(r.GetString(0));
            }
            return keys;
        }
    }

    /// <summary>Records reminders handed to Windows. A task has no occurrence; its row keeps its due time instead, for pruning.</summary>
    public void MarkRemindersScheduled(IEnumerable<Reminder> reminders)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            foreach (var reminder in reminders)
            {
                Execute(_connection, tx,
                    """
                    INSERT INTO reminder_state (key, item_id, occurrence_utc, fire_utc, state) VALUES ($key, $item, $occurrence, $fire, 'scheduled')
                    ON CONFLICT (key) DO NOTHING;
                    """,
                    ("$key", reminder.Key), ("$item", reminder.ItemId),
                    ("$occurrence", (reminder.Recurrence ?? reminder.Start ?? reminder.ShowLateUntil)?.ToUnixTimeMilliseconds()),
                    ("$fire", reminder.FireAt.ToUnixTimeMilliseconds()));
            }
            tx.Commit();
        }
    }

    /// <summary>Removes rows whose reminder and occurrence are both before the cutoff (R4: older than 7 days, on start).</summary>
    public void PruneReminderState(DateTimeOffset before)
    {
        lock (_gate)
        {
            Execute(_connection, null,
                "DELETE FROM reminder_state WHERE fire_utc < $before AND COALESCE(occurrence_utc, fire_utc) < $before;",
                ("$before", before.ToUnixTimeMilliseconds()));
        }
    }
}
