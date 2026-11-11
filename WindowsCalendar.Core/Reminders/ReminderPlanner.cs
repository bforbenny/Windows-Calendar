using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Reminders;

/// <summary>
/// One alarm of one occurrence (reminders-notifications R1, R3): when it fires and what its toast shows.
/// Recurrence is the occurrence's original start, used to open it; ShowLateUntil is when a reminder whose time has
/// passed stops being worth showing (an event's start, a task's due time or the end of its due day; null: never late).
/// </summary>
public sealed record Reminder(
    string ItemId,
    string CollectionId,
    ItemKind Kind,
    DateTimeOffset? Recurrence,
    DateTimeOffset FireAt,
    string Title,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    bool AllDay,
    string? Location,
    string CalendarName,
    string? JoinUrl,
    DateTimeOffset? ShowLateUntil)
{
    /// <summary>Identifies this alarm of this occurrence; the reminder state (R4) is keyed on it.</summary>
    public string Key => $"{ItemId}|{(Recurrence ?? Start)?.ToUnixTimeMilliseconds()}|{FireAt.ToUnixTimeMilliseconds()}";
}

/// <summary>The states a reminder's row can hold (reminders-notifications R2, R4, R6).</summary>
public static class ReminderStates
{
    /// <summary>Handed to Windows' schedule or shown.</summary>
    public const string Scheduled = "scheduled";
    public const string Dismissed = "dismissed";
    public const string Snoozed = "snoozed";
    /// <summary>Joined or opened from its toast.</summary>
    public const string Opened = "opened";
    /// <summary>Listed in a missed-reminder summary.</summary>
    public const string Missed = "missed";
}

/// <summary>A reminder's stored state; SnoozeUntil is set while it is snoozed.</summary>
public sealed record ReminderStatus(string State, DateTimeOffset? SnoozeUntil);

/// <summary>A snoozed reminder whose toast is due again at Until.</summary>
public sealed record SnoozedReminder(Reminder Reminder, DateTimeOffset Until);

/// <summary>Reminders to hand to Windows' schedule, ones to show at once, and snoozed ones to keep scheduled.</summary>
public sealed record ReminderPlan(IReadOnlyList<Reminder> Schedule, IReadOnlyList<Reminder> ShowNow, IReadOnlyList<SnoozedReminder> Snoozed);

/// <summary>Decides which reminders are scheduled and which show at once (reminders-notifications R2).</summary>
public static class ReminderPlanner
{
    /// <summary>How far ahead toasts are scheduled while the app runs.</summary>
    public static readonly TimeSpan RunningWindow = TimeSpan.FromHours(2);

    /// <summary>How far ahead toasts are scheduled on Quit.</summary>
    public static readonly TimeSpan QuitWindow = TimeSpan.FromHours(48);

    /// <summary>A late reminder older than this is skipped, so a first run or a long time off does not bring a flood (R6 covers those).</summary>
    public static readonly TimeSpan LateLimit = TimeSpan.FromHours(2);

    /// <summary>The missed-reminder summary looks back at most this far (R6).</summary>
    public static readonly TimeSpan MissedWindow = TimeSpan.FromHours(12);

    /// <summary>
    /// Reminders firing after now and within the window are scheduled, unless already dismissed or snoozed. One whose
    /// time passed less than <see cref="LateLimit"/> ago and that has no state row shows at once while its item has
    /// not started, else is skipped. A snoozed reminder is kept while it still exists and its snooze is ahead (R4); one
    /// whose item moved, was deleted or completed is no longer among <paramref name="reminders"/>, so it is dropped.
    /// </summary>
    public static ReminderPlan Plan(IEnumerable<Reminder> reminders, IReadOnlyDictionary<string, ReminderStatus> states, DateTimeOffset now, TimeSpan window)
    {
        var schedule = new List<Reminder>();
        var showNow = new List<Reminder>();
        var snoozed = new List<SnoozedReminder>();
        foreach (var r in reminders.DistinctBy(r => r.Key))
        {
            var status = states.GetValueOrDefault(r.Key);
            if (status is { State: ReminderStates.Snoozed, SnoozeUntil: { } until } && until > now)
            {
                snoozed.Add(new SnoozedReminder(r, until));
            }
            else if (r.FireAt > now)
            {
                if (r.FireAt <= now + window && status is null or { State: ReminderStates.Scheduled })
                {
                    schedule.Add(r);
                }
            }
            else if (r.FireAt > now - LateLimit && status is null && r.ShowLateUntil is { } lateUntil && lateUntil > now)
            {
                showNow.Add(r);
            }
        }
        return new ReminderPlan(Ordered(schedule), Ordered(showNow), snoozed.OrderBy(s => s.Until).ThenBy(s => s.Reminder.Key, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// R6: reminders due after <paramref name="since"/> (at most <see cref="MissedWindow"/> ago) and up to now that
    /// Windows did not show (not in <paramref name="inHistory"/>) and that were not dismissed, opened or already
    /// listed. A snoozed reminder counts by its snooze time.
    /// </summary>
    public static IReadOnlyList<Reminder> Missed(IEnumerable<Reminder> reminders, IReadOnlyDictionary<string, ReminderStatus> states,
        IReadOnlySet<string> inHistory, DateTimeOffset since, DateTimeOffset now)
    {
        var from = since > now - MissedWindow ? since : now - MissedWindow;
        return Ordered(reminders.DistinctBy(r => r.Key).Where(r =>
        {
            var status = states.GetValueOrDefault(r.Key);
            var due = status is { State: ReminderStates.Snoozed, SnoozeUntil: { } until } ? until : r.FireAt;
            return due > from && due <= now && !inHistory.Contains(r.Key)
                && status is null or { State: ReminderStates.Scheduled or ReminderStates.Snoozed };
        }).ToList());
    }

    private static List<Reminder> Ordered(List<Reminder> reminders) =>
        reminders.OrderBy(r => r.FireAt).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
}
