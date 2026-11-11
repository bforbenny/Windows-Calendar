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

/// <summary>Reminders to hand to Windows' schedule, and ones to show at once.</summary>
public sealed record ReminderPlan(IReadOnlyList<Reminder> Schedule, IReadOnlyList<Reminder> ShowNow);

/// <summary>Decides which reminders are scheduled and which show at once (reminders-notifications R2).</summary>
public static class ReminderPlanner
{
    /// <summary>How far ahead toasts are scheduled while the app runs.</summary>
    public static readonly TimeSpan RunningWindow = TimeSpan.FromHours(2);

    /// <summary>How far ahead toasts are scheduled on Quit.</summary>
    public static readonly TimeSpan QuitWindow = TimeSpan.FromHours(48);

    /// <summary>A late reminder older than this is skipped, so a first run or a long time off does not bring a flood (R6 covers those).</summary>
    public static readonly TimeSpan LateLimit = TimeSpan.FromHours(2);

    /// <summary>
    /// Reminders firing after now and within the window are scheduled. One whose time passed less than
    /// <see cref="LateLimit"/> ago and that was never handled (not in <paramref name="handledKeys"/>) shows at once
    /// while its item has not started, else is skipped.
    /// </summary>
    public static ReminderPlan Plan(IEnumerable<Reminder> reminders, IReadOnlySet<string> handledKeys, DateTimeOffset now, TimeSpan window)
    {
        var schedule = new List<Reminder>();
        var showNow = new List<Reminder>();
        foreach (var r in reminders.DistinctBy(r => r.Key))
        {
            if (r.FireAt > now)
            {
                if (r.FireAt <= now + window)
                {
                    schedule.Add(r);
                }
            }
            else if (r.FireAt > now - LateLimit && !handledKeys.Contains(r.Key) && r.ShowLateUntil is { } until && until > now)
            {
                showNow.Add(r);
            }
        }
        return new ReminderPlan(
            schedule.OrderBy(r => r.FireAt).ThenBy(r => r.Key, StringComparer.Ordinal).ToList(),
            showNow.OrderBy(r => r.FireAt).ThenBy(r => r.Key, StringComparer.Ordinal).ToList());
    }
}
