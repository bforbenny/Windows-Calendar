---
description: Tray icon flyout showing today's agenda with Join buttons, tasks due, quick add and sync status
when-to-use: Before working on the tray icon, the tray flyout window, or tray menu behaviour
tags: [ui, tray, agenda, quick-add]
status: pending
---

# Tray flyout

## Summary

Left-clicking the tray icon opens a compact flyout with what is on today, Join buttons and a quick-add box. You don't have to open the main window. Right-clicking keeps the existing tray menu (Open, Sync now, Quit).

## Requirements

- R1. Left-click on the tray icon toggles the flyout; right-click shows the context menu ([app-platform](app-platform.md) R5).
- R2. Flyout is a small borderless window anchored to the tray position (respecting taskbar location and multi-monitor DPI), dismissed by clicking outside or Esc. Size: 360 epx wide, height fits content up to 560 epx; the header (date, next event) and the quick-add box and footer stay fixed while the event and task list scrolls.
- R2a. Positioning: first try H.NotifyIcon.Wpf's own tray popup support; if it does not place the flyout correctly at 150% scaling, with the taskbar on another edge, or on a second monitor, place it with the notification-area rectangle from Windows (`Shell_NotifyIconGetRect`) and the work area of that monitor. No new package.
- R3. Content, top to bottom:
  - today's date and the next event countdown ("Next: Standup in 12 min");
  - all-day events in a header row, then events in progress, then the rest of today, then tomorrow (time, colour, title, location); Join appears by the same rule as the views ([online-meeting-join](online-meeting-join.md) R4); declined events are hidden and cancelled ones struck through ([attendees-invitations](attendees-invitations.md) R4–R5);
  - tasks due today/overdue with checkboxes;
  - a quick-add box ([quick-add](quick-add.md));
  - footer: sync status and last sync time, Sync now, Open calendar.
- R4. Clicking an item opens the main window in the current view on that date, with the item open in the editor panel.
- R4a. Content is refreshed each time the flyout opens and at midnight. If two events start at the same time, the "Next" line names both.
- R5. Respects calendar visibility and the active calendar set ([calendar-sets](calendar-sets.md)).
  - Interim (until [calendar-sets](calendar-sets.md) exists): only the sidebar ticks apply.
- R6. Works offline and with the main window hidden; opens in < 150 ms. The flyout window is created once, on first open, and hidden rather than closed, so later opens only refresh its content.- R7. Tray icon tooltip shows the next event and its start time.
- R8. Fully keyboard operable (Tab/arrows, Enter, Esc).

## Out of scope

- Global hotkey to open the flyout.
- Windows 11 widgets board integration.

## Acceptance criteria

- [ ] With the main window closed, left-click on the tray shows today's events and tasks.
- [ ] Join from the flyout opens the meeting.
- [ ] Quick add in the flyout creates an event that syncs.
- [ ] Flyout is positioned correctly with the taskbar at the bottom and on a second monitor.
- [ ] Ticking a task in the flyout completes it (Microsoft To Do); Radicale tasks show a disabled checkbox.

## Dependencies

- [app-platform](app-platform.md), [online-meeting-join](online-meeting-join.md), [quick-add](quick-add.md), [tasks](tasks.md), [calendar-sets](calendar-sets.md)

## Open questions

- WPF has no built-in tray API; the app uses H.NotifyIcon.Wpf (approved 2026-11-08, replacing H.NotifyIcon.WinUI).
