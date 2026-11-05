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

- R1. Reminder sources: CalDAV `VALARM` (DISPLAY action, relative or absolute trigger); Graph event `reminderMinutesBeforeStart` + `isReminderOn`; Graph task `reminderDateTime` + `isReminderOn`.
- R2. Scheduling: a reminder scheduler computes due reminders from the occurrence index for the next 48 hours, and reschedules after every sync/local change, on app start, on resume, and when the Windows timezone changes. Rescheduling cancels or re-times snoozed toasts whose event moved or was deleted. Toasts are scheduled only 48 hours ahead, so after Quit reminders stop once that window runs out ([settings-page](settings-page.md) R7). It uses `ToastNotificationManager` scheduled toasts (Windows App SDK AppNotifications) so toasts fire even if the process is busy.
- R3. Toast content: title, time range (in the display timezone), location, calendar name. Actions: Snooze (5 / 10 / 15 min, 1 h), Dismiss, Open, and Join when the event has a meeting link ([online-meeting-join](online-meeting-join.md)). If the app is not running, a toast action starts it in tray mode to handle the action: Join launches the link, Open shows the main window on that event with the editor open, Snooze and Dismiss are recorded. Clicking the missed-reminder summary (R6) opens the Agenda view on today.
- R4. Dismiss and snooze are local state only (not synced to the server) in v1. They are stored per occurrence in the local store, so rescheduling never re-shows a dismissed reminder or drops a snoozed one.
- R5. Per calendar, in Settings > Calendars:
  - "Reminders on" (default on; off by default for holiday calendars). When off, no toasts fire for that calendar.
  - Default reminder for new timed events (default 15 min). New all-day events get no reminder by default.
  - MS365 events hold one reminder; Radicale events can hold several. "None" is always allowed.
- R6. Missed reminders (machine asleep or off): on resume, show one summary toast for reminders missed within the last 12 hours, rather than a flood.
- R7. No reminders for declined or cancelled events ([attendees-invitations](attendees-invitations.md) R4–R5) or completed tasks.
- R8. Respect Windows Focus / Do Not Disturb (default OS behaviour; no override).

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

## Dependencies

- [app-platform](app-platform.md), [local-store-offline](local-store-offline.md), [timezone-handling](timezone-handling.md)
