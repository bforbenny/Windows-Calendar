using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web;
using System.Windows;
using Serilog;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using WindowsCalendar.Core.Reminders;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// Keeps Windows' toast schedule in line with the store (reminders-notifications R2, R4), handles toast actions (R3,
/// R4) and shows the missed-reminder summary (R6). Each toast's tag is a hash of its reminder and content, so a moved,
/// edited or deleted item replaces or cancels its toast, a snoozed one included.
/// </summary>
internal static class ReminderToasts
{
    private const string ReminderGroup = "reminder";
    private const string SnoozeGroup = "snooze";
    private const string MissedGroup = "missed";
    private static readonly Lock Gate = new();
    private static bool _quitting;

    // Set on start and resume until the summary has run, so the time the app last watched reminders is kept.
    private static bool _missedPending;

    /// <summary>Schedules the next window of reminders; after Quit's wider schedule, does nothing.</summary>
    internal static void Reschedule(LocalStore store, TimeSpan window)
    {
        lock (Gate)
        {
            if (!_quitting)
            {
                Apply(store, window);
            }
        }
    }

    /// <summary>On Quit: schedules 48 hours ahead, and stops later runs from narrowing it again.</summary>
    internal static void RescheduleForQuit(LocalStore store)
    {
        lock (Gate)
        {
            _quitting = true;
            Apply(store, ReminderPlanner.QuitWindow);
        }
    }

    private static void Apply(LocalStore store, TimeSpan window)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var plan = ReminderPlanner.Plan(Collect(store, now), store.GetReminderStates(), now, window);
            var notifier = ToastNotificationManager.CreateToastNotifier();
            var wanted = plan.Schedule.ToDictionary(Tag, r => (r, r.FireAt));
            var snoozes = plan.Snoozed.ToDictionary(s => Tag(s.Reminder), s => (s.Reminder, s.Until));
            var removed = 0;
            foreach (var toast in notifier.GetScheduledToastNotifications())
            {
                // A snoozed toast whose item moved, changed, was deleted or completed is cancelled (R2, R4).
                var keep = toast.Group switch
                {
                    ReminderGroup => wanted.Remove(toast.Tag),
                    SnoozeGroup => snoozes.Remove(toast.Tag),
                    _ => true,
                };
                if (!keep)
                {
                    notifier.RemoveFromSchedule(toast);
                    removed++;
                }
            }
            Add(notifier, wanted, ReminderGroup);
            Add(notifier, snoozes, SnoozeGroup);
            foreach (var reminder in plan.ShowNow)
            {
                notifier.Show(new ToastNotification(Content(reminder)) { Tag = Tag(reminder), Group = ReminderGroup });
            }
            store.MarkRemindersScheduled(plan.Schedule.Concat(plan.ShowNow));
            if (!_missedPending)
            {
                store.SetRemindersAwakeUntil(now);
            }
            Log.Information("Reminders: {Count} in the next {Hours} h ({Added} added, {Removed} removed), {Late} shown late, {Snoozed} snoozed",
                plan.Schedule.Count, (int)window.TotalHours, wanted.Count + snoozes.Count, removed, plan.ShowNow.Count, plan.Snoozed.Count);
        }
        catch (Exception ex)
        {
            Log.Error("Reminder scheduling failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>Schedules toasts not yet in Windows' schedule; one already due is shown at once.</summary>
    private static void Add(ToastNotifier notifier, Dictionary<string, (Reminder Reminder, DateTimeOffset At)> toasts, string group)
    {
        foreach (var (tag, (reminder, at)) in toasts)
        {
            // Windows refuses a delivery time that is not in the future.
            if (at > DateTimeOffset.UtcNow.AddSeconds(5))
            {
                notifier.AddToSchedule(new ScheduledToastNotification(Content(reminder), at) { Tag = tag, Group = group });
            }
            else
            {
                notifier.Show(new ToastNotification(Content(reminder)) { Tag = tag, Group = group });
            }
        }
    }

    /// <summary>On start and on resume: the next <see cref="ShowMissed"/> covers the time since the app last watched.</summary>
    internal static void ExpectMissed()
    {
        lock (Gate)
        {
            _missedPending = true;
        }
    }

    /// <summary>On suspend: the missed-reminder summary after resume starts from now.</summary>
    internal static void Suspending(LocalStore store)
    {
        lock (Gate)
        {
            if (!_missedPending)
            {
                store.SetRemindersAwakeUntil(DateTimeOffset.UtcNow);
            }
        }
    }

    /// <summary>
    /// R6, after start or resume: one summary toast for reminders due since the app last watched (at most 12 hours)
    /// that Windows did not show. On resume it runs after the sync's network work, so toasts Windows shows late on wake
    /// are in its history by then. Does nothing on the first run, when there is no earlier time to start from.
    /// </summary>
    internal static void ShowMissed(LocalStore store)
    {
        lock (Gate)
        {
            if (!_missedPending)
            {
                return;
            }
            _missedPending = false;
            var now = DateTimeOffset.UtcNow;
            try
            {
                if (store.GetRemindersAwakeUntil() is { } since)
                {
                    var missed = ReminderPlanner.Missed(Collect(store, now), store.GetReminderStates(), HistoryKeys(), since, now);
                    if (missed.Count > 0)
                    {
                        ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(MissedContent(missed)) { Group = MissedGroup });
                        store.SetReminderState(missed, ReminderStates.Missed);
                    }
                    Log.Information("Missed reminders since {Since:u}: {Count}", since, missed.Count);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Missed-reminder summary failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
            }
            store.SetRemindersAwakeUntil(now);
        }
    }

    /// <summary>Keys of reminders whose toasts are still in Action Center: Windows showed them.</summary>
    private static HashSet<string> HistoryKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var toast in ToastNotificationManager.History.GetHistory())
        {
            if (HttpUtility.ParseQueryString(toast.Content.DocumentElement.GetAttribute("launch"))["key"] is { Length: > 0 } key)
            {
                keys.Add(key);
            }
        }
        return keys;
    }

    /// <summary>Reminders from 12 hours back on: covers late and snoozed ones and the missed summary.</summary>
    private static IReadOnlyList<Reminder> Collect(LocalStore store, DateTimeOffset now) =>
        ReminderSource.Collect(store, now - ReminderPlanner.MissedWindow, TimeZoneInfo.Local);

    /// <summary>A toast action (R3, R4), on the UI thread. Each action on a reminder is recorded, so it is not shown again.</summary>
    internal static void Handle(string invokedArgs, int snoozeMinutes)
    {
        var args = HttpUtility.ParseQueryString(invokedArgs);
        var key = args["key"] ?? "";
        var app = Application.Current as App;
        var store = app?.Store;
        var reminder = store is null || key.Length == 0 ? null : Find(store, key);
        switch (args["action"])
        {
            case "snooze" when reminder is not null:
                var until = DateTimeOffset.UtcNow.AddMinutes(snoozeMinutes);
                store!.SetReminderState([reminder], ReminderStates.Snoozed, until);
                ToastNotificationManager.CreateToastNotifier().AddToSchedule(
                    new ScheduledToastNotification(Content(reminder), until) { Tag = Tag(reminder), Group = SnoozeGroup });
                Log.Information("Reminder snoozed for {Minutes} min", snoozeMinutes);
                break;
            case "dismiss" when reminder is not null:
                store!.SetReminderState([reminder], ReminderStates.Dismissed);
                break;
            case "join":
                if (reminder is not null)
                {
                    store!.SetReminderState([reminder], ReminderStates.Opened);
                }
                if (reminder is { JoinUrl: { } url } && JoinLink.Recognise(url) is { } link)
                {
                    MeetingJoin.Launch(link);
                }
                break;
            case "open":
                if (reminder is not null)
                {
                    store!.SetReminderState([reminder], ReminderStates.Opened);
                }
                app?.OpenItem(args["item"] ?? "", Time(args["start"]), Time(args["rec"]));
                break;
            case "missed":
                app?.OpenAgendaToday();
                break;
            case "signin":
                // The Sign in bar is in the window (ui-states R2); the prompt opens only from its button.
                app?.ShowMainWindow();
                break;
        }
    }

    /// <summary>The reminder a toast was made from, read again from the store.</summary>
    private static Reminder? Find(LocalStore store, string key) =>
        ReminderSource.Collect(store, DateTimeOffset.UtcNow.AddDays(-1), TimeZoneInfo.Local).FirstOrDefault(r => r.Key == key);

    private static DateTimeOffset? Time(string? ms) =>
        long.TryParse(ms, out var v) ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;

    private static string Tag(Reminder r)
    {
        var text = string.Join('\n', r.Key, r.Title, r.Location, r.CalendarName, r.JoinUrl, r.Start?.ToUnixTimeMilliseconds(),
            r.End?.ToUnixTimeMilliseconds(), r.AllDay);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16);
    }

    /// <summary>R3: title, time in the display timezone, location and calendar; snooze picker, Snooze, Dismiss, Join or Open.</summary>
    private static XmlDocument Content(Reminder r)
    {
        var ids = $"key={Uri.EscapeDataString(r.Key)}&item={Uri.EscapeDataString(r.ItemId)}"
            + $"&start={r.Start?.ToUnixTimeMilliseconds()}&rec={r.Recurrence?.ToUnixTimeMilliseconds()}";
        var where = string.IsNullOrWhiteSpace(r.Location) ? r.CalendarName : $"{r.Location} · {r.CalendarName}";
        var join = JoinLink.Recognise(r.JoinUrl);
        var last = join is not null
            ? $"""<action content="{Escape($"Join {join.Provider}")}" activationType="foreground" arguments="{Escape("action=join&" + ids)}" />"""
            : $"""<action content="Open" activationType="foreground" arguments="{Escape("action=open&" + ids)}" />""";
        var xml = $"""
            <toast launch="{Escape("action=open&" + ids)}" scenario="reminder">
              <visual><binding template="ToastGeneric">
                <text>{Escape(r.Title)}</text>
                <text>{Escape(When(r))}</text>
                <text>{Escape(where)}</text>
              </binding></visual>
              <actions>
                <input id="snoozeMinutes" type="selection" defaultInput="5">
                  <selection id="5" content="5 minutes" />
                  <selection id="10" content="10 minutes" />
                  <selection id="15" content="15 minutes" />
                  <selection id="60" content="1 hour" />
                </input>
                <action content="Snooze" activationType="background" arguments="{Escape("action=snooze&" + ids)}" />
                <action content="Dismiss" activationType="background" arguments="{Escape("action=dismiss&" + ids)}" />
                {last}
              </actions>
            </toast>
            """;
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc;
    }

    /// <summary>R6: how many were missed, the first three with their times, and how many more; opens Agenda on today.</summary>
    private static XmlDocument MissedContent(IReadOnlyList<Reminder> missed)
    {
        var list = string.Join(", ", missed.Take(3).Select(r =>
            $"{r.Title} ({TimeZoneInfo.ConvertTime(r.Start ?? r.FireAt, TimeZoneInfo.Local):ddd t})"));
        var more = missed.Count > 3 ? $"<text>{Escape($"and {missed.Count - 3} more")}</text>" : "";
        var xml = $"""
            <toast launch="action=missed">
              <visual><binding template="ToastGeneric">
                <text>{Escape(missed.Count == 1 ? "1 missed reminder" : $"{missed.Count} missed reminders")}</text>
                <text>{Escape(list)}</text>
                {more}
              </binding></visual>
            </toast>
            """;
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc;
    }

    /// <summary>
    /// "Sign in to Work to resume syncing" while the window is hidden (ui-states R2, sync-ms365 R11); a click opens the
    /// window, where the Sign in bar is.
    /// </summary>
    internal static void ShowSignIn(string accountName)
    {
        var xml = $"""
            <toast launch="action=signin">
              <visual><binding template="ToastGeneric">
                <text>{Escape($"Sign in to {accountName}")}</text>
                <text>Syncing is paused until you sign in again.</text>
              </binding></visual>
            </toast>
            """;
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(doc));
            Log.Information("Sign-in toast shown");
        }
        catch (Exception ex)
        {
            Log.Error("Sign-in toast failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
        }
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";

    private static string When(Reminder r)
    {
        if (r.Start is not { } startUtc)
        {
            return "Task";
        }
        var start = TimeZoneInfo.ConvertTime(startUtc, TimeZoneInfo.Local);
        var day = DateFormats.WeekdayMonthDay(System.Globalization.CultureInfo.CurrentCulture);
        if (r.Kind == ItemKind.Task)
        {
            return r.AllDay ? $"Due {start.ToString(day)}" : $"Due {start.ToString(day)}, {start:t}";
        }
        if (r.AllDay)
        {
            return $"{start.ToString(day)}, all day";
        }
        var end = TimeZoneInfo.ConvertTime(r.End ?? startUtc, TimeZoneInfo.Local);
        return end.Date == start.Date
            ? $"{start.ToString(day)}, {start:t} – {end:t}"
            : $"{start.ToString(day)}, {start:t} – {end.ToString(day)}, {end:t}";
    }
}
