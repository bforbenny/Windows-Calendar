using WindowsCalendar.Core.Reminders;

namespace WindowsCalendar.Core.Store;

/// <summary>Per-reminder local state (reminders-notifications R2, R4, R6); never synced.</summary>
public sealed partial class LocalStore
{
    private const string RemindersAwakeKey = "reminders:awake";

    /// <summary>Every reminder's stored state by key; a reminder with a row is never shown late.</summary>
    public IReadOnlyDictionary<string, ReminderStatus> GetReminderStates()
    {
        lock (_gate)
        {
            using var cmd = Command(_connection, null, "SELECT key, state, snooze_until FROM reminder_state;");
            using var r = cmd.ExecuteReader();
            var states = new Dictionary<string, ReminderStatus>(StringComparer.Ordinal);
            while (r.Read())
            {
                states[r.GetString(0)] = new ReminderStatus(r.GetString(1),
                    r.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)));
            }
            return states;
        }
    }

    /// <summary>Records reminders handed to Windows; a reminder that already has a row keeps it.</summary>
    public void MarkRemindersScheduled(IEnumerable<Reminder> reminders) => Save(reminders, ReminderStates.Scheduled, null, replace: false);

    /// <summary>Sets the state of reminders (dismissed, snoozed until a time, opened, missed), replacing any earlier state (R4).</summary>
    public void SetReminderState(IEnumerable<Reminder> reminders, string state, DateTimeOffset? snoozeUntil = null) =>
        Save(reminders, state, snoozeUntil, replace: true);

    /// <summary>A task has no occurrence; its row keeps its due time instead, for pruning.</summary>
    private void Save(IEnumerable<Reminder> reminders, string state, DateTimeOffset? snoozeUntil, bool replace)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            foreach (var reminder in reminders)
            {
                Execute(_connection, tx,
                    $"""
                    INSERT INTO reminder_state (key, item_id, occurrence_utc, fire_utc, state, snooze_until)
                    VALUES ($key, $item, $occurrence, $fire, $state, $until)
                    ON CONFLICT (key) DO {(replace ? "UPDATE SET state = excluded.state, snooze_until = excluded.snooze_until" : "NOTHING")};
                    """,
                    ("$key", reminder.Key), ("$item", reminder.ItemId),
                    ("$occurrence", (reminder.Recurrence ?? reminder.Start ?? reminder.ShowLateUntil)?.ToUnixTimeMilliseconds()),
                    ("$fire", reminder.FireAt.ToUnixTimeMilliseconds()), ("$state", state),
                    ("$until", snoozeUntil?.ToUnixTimeMilliseconds()));
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

    /// <summary>Until when the app last watched reminders (R6): set while it runs and on suspend; null before the first run.</summary>
    public DateTimeOffset? GetRemindersAwakeUntil()
    {
        lock (_gate)
        {
            return long.TryParse(GetMeta(null, RemindersAwakeKey), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        }
    }

    public void SetRemindersAwakeUntil(DateTimeOffset at)
    {
        lock (_gate)
        {
            SetMeta(null, RemindersAwakeKey, at.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
