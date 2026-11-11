using System.Globalization;

namespace WindowsCalendar.Core.Reminders;

/// <summary>
/// An event's reminder in the editor (reminders-notifications R5a): minutes before the start (negative: after it, e.g.
/// 09:00 on an all-day event's day), or none. A reminder the drop-down cannot set (a fixed time, or one relative to the
/// end) is <see cref="Kept"/>: shown by its label and written back unchanged.
/// </summary>
public sealed record ReminderChoice(int? MinutesBefore, string? Kept = null)
{
    public static readonly ReminderChoice None = new((int?)null);

    public static ReminderChoice Before(int minutes) => new(minutes);

    public bool IsNone => MinutesBefore is null && Kept is null;
}

/// <summary>The editor's reminder lists and labels (R5, R5a).</summary>
public static class ReminderChoices
{
    /// <summary>A new timed event's reminder when its calendar has no default set (R5).</summary>
    public const int DefaultMinutes = 15;

    /// <summary>Timed events: None, At start, 5, 10, 15, 30 min, 1 h, 2 h, 1 day, 2 days before.</summary>
    public static readonly IReadOnlyList<ReminderChoice> Timed =
        [ReminderChoice.None, .. new[] { 0, 5, 10, 15, 30, 60, 120, 1440, 2880 }.Select(ReminderChoice.Before)];

    /// <summary>
    /// All-day events: None, On the day 09:00, 1 day before 09:00. MS365 takes minutes before the start only, so
    /// it does not offer On the day.
    /// </summary>
    public static IReadOnlyList<ReminderChoice> AllDay(bool graph) =>
        graph ? [ReminderChoice.None, ReminderChoice.Before(900)] : [ReminderChoice.None, ReminderChoice.Before(-540), ReminderChoice.Before(900)];

    public static string Label(ReminderChoice choice, bool allDay)
    {
        if (choice.Kept is { } kept)
        {
            return kept;
        }
        if (choice.MinutesBefore is not { } m)
        {
            return "None";
        }
        if (allDay)
        {
            // The day the reminder falls on, counted back from the start's midnight, and its time of day.
            var days = (int)Math.Ceiling(m / 1440.0);
            var time = TimeSpan.FromMinutes(days * 1440 - m).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
            return days switch
            {
                0 => $"On the day, {time}",
                < 0 => $"{-days} {Plural(-days, "day")} after, {time}",
                _ => $"{days} {Plural(days, "day")} before, {time}",
            };
        }
        return m switch
        {
            0 => "At start",
            < 0 => $"{Duration(-m)} after start",
            _ => $"{Duration(m)} before",
        };
    }

    /// <summary>Minutes as the largest whole unit: "15 minutes", "2 hours", "1 day".</summary>
    private static string Duration(int minutes) =>
        minutes % 1440 == 0 ? $"{minutes / 1440} {Plural(minutes / 1440, "day")}"
        : minutes % 60 == 0 ? $"{minutes / 60} {Plural(minutes / 60, "hour")}"
        : $"{minutes} {Plural(minutes, "minute")}";

    private static string Plural(int n, string unit) => n == 1 ? unit : unit + "s";
}
