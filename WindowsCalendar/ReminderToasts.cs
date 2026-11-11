using System;
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
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// Keeps Windows' toast schedule in line with the store (reminders-notifications R2) and handles toast actions (R3).
/// Each toast's tag is a hash of its reminder and content, so a moved, edited or deleted item replaces its toast.
/// </summary>
internal static class ReminderToasts
{
    private const string ReminderGroup = "reminder";
    private const string SnoozeGroup = "snooze";
    private static readonly Lock Gate = new();
    private static bool _quitting;

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
            var plan = ReminderPlanner.Plan(ReminderSource.Collect(store, now, TimeZoneInfo.Local), store.GetHandledReminderKeys(), now, window);
            var notifier = ToastNotificationManager.CreateToastNotifier();
            var wanted = plan.Schedule.ToDictionary(Tag);
            var removed = 0;
            foreach (var toast in notifier.GetScheduledToastNotifications())
            {
                if (toast.Group == ReminderGroup && !wanted.Remove(toast.Tag))
                {
                    notifier.RemoveFromSchedule(toast);
                    removed++;
                }
            }
            foreach (var (tag, reminder) in wanted)
            {
                // Windows refuses a delivery time that is not in the future.
                if (reminder.FireAt > DateTimeOffset.UtcNow.AddSeconds(5))
                {
                    notifier.AddToSchedule(new ScheduledToastNotification(Content(reminder), reminder.FireAt) { Tag = tag, Group = ReminderGroup });
                }
                else
                {
                    notifier.Show(new ToastNotification(Content(reminder)) { Tag = tag, Group = ReminderGroup });
                }
            }
            foreach (var reminder in plan.ShowNow)
            {
                notifier.Show(new ToastNotification(Content(reminder)) { Tag = Tag(reminder), Group = ReminderGroup });
            }
            store.MarkRemindersScheduled(plan.Schedule.Concat(plan.ShowNow));
            Log.Information("Reminders: {Count} in the next {Hours} h ({Added} added, {Removed} removed), {Late} shown late",
                plan.Schedule.Count, (int)window.TotalHours, wanted.Count, removed, plan.ShowNow.Count);
        }
        catch (Exception ex)
        {
            Log.Error("Reminder scheduling failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>A toast action (R3), on the UI thread. Dismiss only closes the toast until R4 records it.</summary>
    internal static void Handle(string invokedArgs, int snoozeMinutes)
    {
        var args = HttpUtility.ParseQueryString(invokedArgs);
        var key = args["key"] ?? "";
        var app = Application.Current as App;
        switch (args["action"])
        {
            case "snooze":
                if (app?.Store is { } store && Find(store, key) is { } reminder)
                {
                    var toast = new ScheduledToastNotification(Content(reminder), DateTimeOffset.UtcNow.AddMinutes(snoozeMinutes))
                    {
                        Tag = Tag(reminder),
                        Group = SnoozeGroup,
                    };
                    ToastNotificationManager.CreateToastNotifier().AddToSchedule(toast);
                    Log.Information("Reminder snoozed for {Minutes} min", snoozeMinutes);
                }
                break;
            case "join":
                if (app?.Store is { } joinStore && Find(joinStore, key) is { JoinUrl: { } url } && JoinLink.Recognise(url) is { } link)
                {
                    MeetingJoin.Launch(link);
                }
                break;
            case "open":
                app?.OpenFromReminder(args["item"] ?? "", Time(args["start"]), Time(args["rec"]));
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

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";

    private static string When(Reminder r)
    {
        if (r.Start is not { } startUtc)
        {
            return "Task";
        }
        var start = TimeZoneInfo.ConvertTime(startUtc, TimeZoneInfo.Local);
        if (r.Kind == ItemKind.Task)
        {
            return r.AllDay ? $"Due {start:ddd d MMM}" : $"Due {start:ddd d MMM}, {start:t}";
        }
        if (r.AllDay)
        {
            return $"{start:ddd d MMM}, all day";
        }
        var end = TimeZoneInfo.ConvertTime(r.End ?? startUtc, TimeZoneInfo.Local);
        return end.Date == start.Date
            ? $"{start:ddd d MMM}, {start:t} – {end:t}"
            : $"{start:ddd d MMM}, {start:t} – {end:ddd d MMM}, {end:t}";
    }
}
