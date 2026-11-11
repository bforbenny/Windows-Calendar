---
description: Warn when a created, edited, moved or resized event overlaps another busy event or leaves less than the minimum gap
when-to-use: Before working on clash or gap checks, the minimum-gap setting, or warnings in the editor and the drag banner
tags: [events, editor, drag-drop, settings, banners]
status: pending
---

# Schedule gap warning

## Summary

When the user saves an event, the app checks it against the neighbouring busy events and warns about an overlap, or about a gap shorter than a set minimum (buffer time). Neither MS365 (Graph) nor Radicale (CalDAV) has a shared buffer field, so this is a local check only: nothing is stored on the event or synced. The warning never blocks saving. Decided 2026-11-11 after a market comparison (Apple Calendar travel time, Outlook, Google speedy meetings, Reclaim/Motion buffers), in place of buffer events or a local buffer field.

## Requirements

- R1. Trigger: the check runs when an event is created, edited (start, end, all-day or show-as changed), moved or resized ([calendar-views](calendar-views.md) R5–R7, [keyboard-navigation](keyboard-navigation.md)), duplicated ([duplicate-event](duplicate-event.md)), or created by quick add in the main window ([quick-add](quick-add.md)). It does not run for events the user does not change.
- R2. What counts: the event being saved and the events it is compared with are timed (not all-day), on calendars ticked in the sidebar, and shown as Busy, Tentative, Out of office or Working elsewhere ([event-show-as](event-show-as.md)). Free events, all-day events, tasks and cancelled occurrences are ignored. If the saved event is itself Free or all-day, no check runs.
- R3. Overlap: another counted event starts before the saved event ends and ends after it starts. This is a warning.
- R4. Short gap: no overlap, but the time between the saved event and the nearest counted event before or after it is greater than 0 and less than the minimum gap. Back-to-back events (gap 0) also count as a short gap when the minimum is above 0. This is a softer note.
- R5. Setting: "Minimum gap between events" in Settings > Calendars ([settings-page](settings-page.md)): Off, 5, 10, 15, 30 minutes. Default 10 minutes. Off disables R4 only; the overlap warning always runs. Stored locally (settings-page R5).
- R6. Recurring events: only the occurrence being saved is checked (for a series edit, the occurrence the user acted on).
- R7. In the editor: a single line under the start/end fields, warning style for an overlap ("Overlaps Weekly sync 10:00–10:30"), informational style for a short gap ("5 min after Weekly sync, less than your 10 min gap"). With several clashes it names the first and adds "+N more". It updates as the times change and clears when resolved.
- R8. After a drag, resize, keyboard move or duplicate: the text is added to the existing undo banner ([floating-banners](floating-banners.md), [undo](undo.md)), e.g. "Event moved · Overlaps Weekly sync · Undo", using warning severity for an overlap. No separate banner is added.
- R9. The check reads the local store only, so it works offline and adds no server calls.

## Out of scope

- Buffer or travel-time events created on the server, and a per-event buffer field.
- Free/busy of other people, and attendee availability.
- Checks from the tray flyout quick add ([tray-flyout](tray-flyout.md)), which has no editor or banner.
- Checking every occurrence of a recurring series.
- Persistent clash markers on events in the grid.
- Blocking or auto-moving events.
- Scheduling tasks as time blocks.

## Acceptance criteria

- [ ] Dragging an event onto a busy event shows "Overlaps …" in the undo banner; Undo restores it.
- [ ] Creating an event 5 minutes after a busy event with the default setting shows the short-gap note in the editor; changing the start to 10 minutes after clears it.
- [ ] With the setting Off, a back-to-back event shows nothing, and an overlapping one still warns.
- [ ] An overlap with a Free event, an all-day event, a task, or an event on an unticked calendar shows nothing.
- [ ] The check works with the network disconnected.

## Dependencies

- [calendar-views](calendar-views.md), [event-show-as](event-show-as.md), [floating-banners](floating-banners.md), [undo](undo.md), [settings-page](settings-page.md), [recurring-events](recurring-events.md), [local-store-offline](local-store-offline.md)
