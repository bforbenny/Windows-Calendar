---
description: Warn when a created, edited, moved or resized event overlaps another busy event or leaves less than the minimum gap
when-to-use: Before working on clash or gap checks, the minimum-gap setting, or warnings in the editor and the drag banner
tags: [events, editor, drag-drop, settings, banners]
status: closed
---

# Schedule gap warning

## Summary

When the user saves an event, the app checks it against the neighbouring busy events and warns about an overlap, or about a gap shorter than a set minimum (buffer time). Neither MS365 (Graph) nor Radicale (CalDAV) has a shared buffer field, so this is a local check only: nothing is stored on the event or synced. The warning never blocks saving. Decided 2026-11-11 after a market comparison (Apple Calendar travel time, Outlook, Google speedy meetings, Reclaim/Motion buffers), in place of buffer events or a local buffer field.

## Requirements

- R1. Trigger: the check runs when an event is created, edited (start, end, all-day or show-as changed), moved or resized ([calendar-views](calendar-views.md) R5–R7, [keyboard-navigation](keyboard-navigation.md)), duplicated ([duplicate-event](duplicate-event.md)), or created by quick add in the main window ([quick-add](quick-add.md)). It does not run for events the user does not change.
- R2. What counts: the event being saved and the events it is compared with are timed (not all-day), on calendars that are members of the active calendar set ([calendar-sets](calendar-sets.md); "All" means every calendar), whether ticked in the sidebar or not, and shown as Busy, Tentative, Out of office or Working elsewhere ([event-show-as](event-show-as.md)). Free events, all-day events, declined invitations ([attendees-invitations](attendees-invitations.md) R4's declined, kept as a stored flag), tasks, cancelled occurrences and other occurrences of the saved event's own series are ignored. If the saved event is itself Free or all-day, no check runs.
- R3. Overlap: another counted event starts before the saved event ends and ends after it starts. This is a warning.
- R4. Short gap: no overlap, but the time between the saved event and the nearest counted event before or after it is greater than 0 and less than the minimum gap. Back-to-back events (gap 0) also count as a short gap when the minimum is above 0. This is a softer note.
- R5. Setting: "Minimum gap between events" in Settings > Calendars ([settings-page](settings-page.md)): Off, 5, 10, 15, 30 minutes. Default 10 minutes. Off disables R4 only; the overlap warning always runs. Stored locally (settings-page R5).
- R6. Recurring events: only the occurrence being saved is checked (for a series edit, the occurrence the user acted on).
- R7. In the editor: a single line under the start/end fields, warning style for an overlap ("Overlaps Weekly sync 10:00–10:30"), informational style for a short gap ("5 min after Weekly sync, less than your 10 min gap"; back-to-back: "Right after Weekly sync"). With several clashes it names the first and adds "+N more". It updates as the times change and clears when resolved.
- R8. After a drag, resize, keyboard move or duplicate: the text is added to the existing undo banner ([floating-banners](floating-banners.md), [undo](undo.md)), e.g. "Event moved · Overlaps Weekly sync · Undo", using warning severity for an overlap. No separate banner is added.
- R9. The check reads the local store only, so it works offline and adds no server calls.
- R10. Per set: each calendar set has a "Warn about clashes and short gaps" switch ([calendar-sets](calendar-sets.md) R12). With it off for the active set, no check runs (neither R3 nor R4). "All" starts on; a user-defined set starts off. The minimum gap (R5) is one setting for all sets. The same switch governs the view indicators (R11).
- R11. View indicators: with the active set's switch on, each view marks the problems among the events it shows (calendars ticked in the sidebar only, so a marker always points at visible events), counted as in R2 and found as in R3 and R4 between any two of them:
  - Week / Day: each card involved gets a 2 epx hazard outline over it, alternating yellow and red dashes, the same for an overlap and a short gap; the card's calendar colour bar stays. A warning icon in the same colour sits at the day column's right edge where the problem starts. The outline takes no clicks, so cards drag and open as before.
  - Month: a warning icon beside the day number, caution colour when the day has an overlap, attention colour for short gaps only; each chip involved gets the same hazard outline as in Week.
  - Agenda: the same icon beside the day's date heading, and the same hazard outline on each card involved.
  - Each indicator has a tooltip and screen-reader name, e.g. "Weekly sync overlaps Review 10:00–10:15" (Week) or "1 overlap, 2 short gaps" (Month, Agenda). Icon and text always go with the colour ([ui-colour-theme](ui-colour-theme.md) R4).
  - The indicators update with every render, so events arriving by sync, tray quick add or another device are marked too.

## Decisions

- 2026-11-12: Month chips and Agenda cards get the same hazard outline (user's choice).
- 2026-11-12: the Week outline is one yellow-and-red dashed hazard outline for both overlaps and short gaps (user's choice); the icons keep caution for an overlap and attention for a short gap.
- 2026-11-12, after the in-app check: Week marks the cards with an outline instead of a 4 epx band, which was hardly visible; Month and Agenda use the warning icon for short gaps too, told apart by colour (user's choice).
- 2026-11-12: declined invitations are ignored like Free events; the view indicators of R11 are added (user's choice: Week band over the problem time; overlaps and short gaps; shown calendars only; same per-set switch). This removes "persistent clash markers in the grid" from Out of scope.
- 2026-11-12: the check compares the active set's members and is switched on per set (user's choice); this replaces "calendars ticked in the sidebar". Earlier answers kept (2026-11-11): other occurrences of the event's own series are ignored, and a back-to-back neighbour reads "Right after Weekly sync".

## Out of scope

- Buffer or travel-time events created on the server, and a per-event buffer field.
- Free/busy of other people, and attendee availability.
- Checks from the tray flyout quick add ([tray-flyout](tray-flyout.md)), which has no editor or banner.
- Checking every occurrence of a recurring series.
- Blocking or auto-moving events.
- Scheduling tasks as time blocks.

## Acceptance criteria

- [ ] Dragging an event onto a busy event shows "Overlaps …" in the undo banner; Undo restores it.
- [ ] Creating an event 5 minutes after a busy event with the default setting shows the short-gap note in the editor; changing the start to 10 minutes after clears it.
- [ ] With the setting Off, a back-to-back event shows nothing, and an overlapping one still warns.
- [ ] An overlap with a Free event, an all-day event, a task, or an event on a calendar outside the active set shows nothing.
- [ ] An overlap with an event on a calendar in the active set but unticked in the sidebar still warns.
- [ ] With the set's switch off, an overlapping event shows nothing; turning it on in Settings > Calendar sets makes it warn.
- [ ] The check works with the network disconnected.
- [ ] A declined invitation overlapping an event gives no warning and no indicator.
- [ ] Two overlapping Busy events on shown calendars get the hazard outline in Week, and a warning icon beside the date in Month and Agenda; unticking one calendar removes them.
- [ ] Two events 5 minutes apart (default gap) get the hazard outline in Week and an attention-coloured warning icon beside the date.

## Dependencies

- [calendar-views](calendar-views.md), [event-show-as](event-show-as.md), [floating-banners](floating-banners.md), [undo](undo.md), [settings-page](settings-page.md), [recurring-events](recurring-events.md), [local-store-offline](local-store-offline.md)
