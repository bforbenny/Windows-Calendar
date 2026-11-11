---
description: Windows toast reminders for events and tasks, firing even when the main window is closed
when-to-use: Before working on reminders, alarms, VALARM/reminder mapping, or toast notifications
tags: [reminders, notifications, toast, valarm]
status: pending
---

# Reminders and notifications

## Summary

Event and task reminders show as Windows toast notifications with Snooze and Dismiss actions. They are scheduled from the local store, so they fire offline and while the app sits in the tray.

## Requirements

- R1. Reminder sources: CalDAV `VALARM` (DISPLAY action, relative or absolute trigger) on events and on tasks (VTODO); Graph event `reminderMinutesBeforeStart` + `isReminderOn`; Graph task `reminderDateTime` + `isReminderOn`. Event reminders come from the occurrence index (one per occurrence of a recurring event); task reminders are read from the tasks table, since tasks are not in the occurrence index.
- R2. Scheduling: a reminder scheduler computes due reminders and reschedules after every sync/local change, on app start, on resume from sleep, and when the Windows timezone or clock changes (the app listens for these system events). Rescheduling cancels or re-times snoozed toasts whose event moved or was deleted. The scheduler is a pure function in `WindowsCalendar.Core` (items + dismiss/snooze state + now → toasts to schedule), unit-tested.
  - Mechanism: WinRT `Windows.UI.Notifications` scheduled toasts (`ScheduledToastNotification` through `ToastNotificationManager.CreateToastNotifier()`), available from the app's Windows target framework with no extra package. The Windows App SDK `AppNotifications` API is not used: it cannot schedule toasts and the app does not reference the Windows App SDK ([app-platform](app-platform.md) R7). Each scheduled toast carries an id derived from the item and occurrence, so it can be found and removed.
  - Window: while the app runs, toasts are scheduled 2 hours ahead and topped up every 30 minutes, so a long sleep releases at most 2 hours of reminders on wake. On Quit the window widens to 48 hours, so reminders keep firing for up to 48 hours after Quit ([settings-page](settings-page.md) R7).  - Toast actions are handled by a COM activator declared in the package manifest (`windows.comServer` + `windows.toastNotificationActivation`), implemented in the app with `INotificationActivationCallback` and no toolkit package. When the app is not running, Windows starts it with an activation argument; it starts in tray mode, handles the action, and stays in the tray.
- R3. Toast content: title, time range (in the display timezone), location, calendar name. Actions: a snooze-time picker (5 / 10 / 15 min, 1 h; default 5 min) with a Snooze button, Dismiss, and Join when the event has a meeting link, else Open; clicking the toast body opens the item ([online-meeting-join](online-meeting-join.md)). If the app is not running, a toast action starts it in tray mode to handle the action: Join launches the link, Open shows the main window on that event with the editor open, Snooze and Dismiss are recorded. Clicking the missed-reminder summary (R6) opens the Agenda view on today.
- R4. Dismiss and snooze are local state only (not synced to the server) in v1. They are stored per occurrence in a new local-store table (item id, occurrence start, state, snooze-until; added by a schema migration), so rescheduling never re-shows a dismissed reminder or drops a snoozed one. Rows for occurrences more than 7 days past are pruned on start.
- R5. Per calendar, in Settings > Calendars:
  - "Reminders on" (default on; off by default for holiday calendars). When off, no toasts fire for that calendar.
  - Default reminder for new timed events (default 15 min). New all-day events get no reminder by default.
  - MS365 events hold one reminder; Radicale events can hold several. "None" is always allowed.
- R5a. Editors:
  - The event editor has one Reminder drop-down: None, At start, 5, 10, 15, 30 min, 1 h, 2 h, 1 day, 2 days before (all-day events: None, On the day 09:00, 1 day before 09:00). A value from sync that is not in the list shows as an extra entry and is kept. New events take the calendar's default (R5).
  - On a Radicale event the drop-down edits the first DISPLAY alarm; any other alarms are kept unchanged, and the editor notes "+N more reminders (kept)". Editing several reminders on one Radicale event is deferred ([calendar-views](calendar-views.md) R6 lists it).
  - The task editor has a Reminder date and time (None allowed) for Microsoft To Do tasks. Radicale tasks stay view-only ([tasks](tasks.md)), but their alarms still fire.
  - Reminder edits sync and undo like any other field ([undo](undo.md)).
- R6. Missed reminders (machine asleep or off): on resume, show one summary toast for reminders missed within the last 12 hours, rather than a flood.
- R7. No reminders for declined or cancelled events ([attendees-invitations](attendees-invitations.md) R4–R5) or completed tasks.
- R8. Respect Windows Focus / Do Not Disturb (default OS behaviour; no override).

## Prerequisite checks

Before the scheduler and editors are built, a minimal build checks the two riskiest assumptions on this PC:

1. A scheduled toast's Snooze, Dismiss and Join buttons reach the COM activator both with the app in the tray and after Quit.
2. What Windows does with scheduled toasts that come due while the PC sleeps: shown late on wake, or dropped. The answer sets the scheduling window (R2).

Results are recorded here before the rest of the work starts.

- Check 1, 2026-11-11 (build 1.0.3.0, Release, registered layout): passed. Scheduled toasts showed with the snooze picker, Snooze, Dismiss and Join. With the app running, Snooze (10 min, re-fired on time), Dismiss and Join (opened the link) reached the COM activator. With the app quit, Dismiss started the app in the tray without a window and handled the action. Scheduled toasts survived re-registering the package.
- Check 2: dropped by the user on 2026-11-11 as not required. The R2 window stays as written: 2 hours ahead while running, 48 hours on Quit.

## Out of scope

- Syncing dismiss/snooze state to the servers.
- Email/audio alarm actions (`VALARM ACTION=EMAIL/AUDIO` are preserved but not executed).
- Leave-by/travel-time alerts.

## Acceptance criteria

- [ ] A reminder fires with the window closed (tray only) and with the network off.
- [ ] Snooze re-fires after the chosen interval.
- [ ] Changing an event time on the server reschedules its reminder after sync.
- [ ] After sleep through several reminders, a single summary toast appears.
- [ ] With the app quit, Join on a toast opens the meeting.
- [ ] A dismissed reminder does not reappear after a sync.
- [ ] Turning "Reminders on" off for a calendar stops its toasts.
- [ ] Setting a reminder in the event editor fires a toast at that time; on a Radicale event with two alarms, the second alarm survives the edit.
- [ ] A Microsoft To Do task with a reminder time fires a toast; a completed task does not.

## Dependencies

- [app-platform](app-platform.md), [local-store-offline](local-store-offline.md), [timezone-handling](timezone-handling.md), [tasks](tasks.md), [undo](undo.md)
- Interim (until [timezone-handling](timezone-handling.md) exists): the display timezone is the Windows timezone.
