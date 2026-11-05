---
description: Day, Week, Month (and Agenda) calendar views with navigation, calendar colours, event create/edit, and drag to move/resize
when-to-use: Before working on calendar UI, view rendering, or the event editor
tags: [ui, views, events, drag-drop]
status: pending
---

# Calendar views

## Summary

The main UI provides Day, Week and Month views of all visible calendars across accounts. Events are colour-coded by calendar. Events can be created, opened, edited, moved and resized directly in the views.

## Requirements

- R1. Views: Day, Week (configurable first day of week, default from Windows locale), Month, Agenda (see [agenda-view](agenda-view.md)) and Tasks (see [tasks](tasks.md)). Toolbar with Today, Previous/Next, view switcher, quick add, Sync now, and sync status.
- R2. A sidebar contains:
  - the calendar set selector ([calendar-sets](calendar-sets.md));
  - accounts → calendars and task lists, each with a visibility checkbox and colour swatch; the checkbox is the only per-calendar visibility control and works as an override of the active set ([calendar-sets](calendar-sets.md) R5);
  - a mini month navigator.
- R2a. Calendar colours and shapes, per [ui-colour-theme](ui-colour-theme.md) R6–R9:
  - each calendar or task list has one of 24 colours (8 hues × 3 levels) and a shape marker (up triangle, down triangle, square, circle, pentagon), shown by default;
  - the initial colour is the server colour (Graph `hexColor` / `color`; CalDAV `calendar-color`) snapped to the nearest of the 24, or else an automatic assignment;
  - the user can override the colour and the shape per calendar or task list;
  - overrides are stored locally and not written back to the server;
  - event cards use the fill, bar and text steps defined in [ui-colour-theme](ui-colour-theme.md), which meet WCAG AA in both themes.
- R3. All-day / multi-day events are shown in an all-day lane in Day/Week, and as bars in Month.
- R4. Overlapping events in Day/Week are laid out side by side.
- R4a. Short events in Day/Week are drawn at their true height. Below 24 epx tall they show only the coloured bar and fill, with no text; the title and time appear in a tooltip on hover or keyboard focus, and the event's accessible name is always set for screen readers.
- R5. Create: click or drag on an empty slot → quick-create popup with the typed quick-add box ([quick-add](quick-add.md)) and calendar → "More details" opens the full editor. Date and time typed in the box override the clicked or dragged slot.
- R6. Full event editor fields: title, start/end, all-day, timezone (see [timezone-handling](timezone-handling.md)), location (see [event-location](event-location.md)), description (plain text; read-only when the Graph body is HTML, with the note "Formatted description. Edit in Outlook." and Open in Outlook, so formatting and Teams join blocks are never overwritten), calendar, reminder (one on MS365; one or more on Radicale; "None" allowed), attendees (read-only, [attendees-invitations](attendees-invitations.md) R1), recurrence (see [recurring-events](recurring-events.md)), show-as (see [event-show-as](event-show-as.md)). A Join button is shown when the event has a meeting link ([online-meeting-join](online-meeting-join.md)).
- R6b. Opening events:
  - a single click (pointer released with less than 4 epx movement) opens the event in the editor; more movement starts a drag (R7);
  - keyboard: arrows select, Enter opens;
  - the opened event is also the selected event (Del, Ctrl+D, Ctrl+J act on it);
  - read-only events (R9) open the editor in view-only mode, with Join and (for attendee meetings) your own reminder still available.
- R6c. The editor is a side panel on the right, 400 epx wide. At window width ≥ 1200 epx it pushes the grid narrower; below 1200 epx it slides over the grid. Esc or the close button closes it.
- R6d. The editor auto-saves; there is no Save button:
  - each field change is saved when the field loses focus (or immediately for pickers and checkboxes), queued in the outbox, and is one undo step ([undo](undo.md));
  - for a recurring event, the scope (this / this and following / all) is asked once, at the first change, and applies to every change until the panel closes or switches to another event;
  - clicking another event switches the panel to it, with nothing to confirm;
  - after a "this and following" split, later changes in the same session apply to the new series as "all";
  - a new event opened from "More details" is created once it has a title; closing the panel while the title is empty discards it;
  - changing the calendar of a new event that has not synced yet re-targets its outbox create without asking; show-as values the new backend does not support reset to Busy;
  - Ctrl+Z while a text box has focus undoes typing in that box; otherwise it is the app undo ([undo](undo.md) R3);
  - meetings with attendees follow [attendees-invitations](attendees-invitations.md) R7–R8.
- R6a. Events can be duplicated ([duplicate-event](duplicate-event.md)). Non-working hours, public holidays and week numbers are shown per [calendar-shading](calendar-shading.md).
- R7. Drag to move and drag the edge to resize in Day/Week. Drag to move between days in Month. For recurring events, a drag always asks for the scope (this / this and following / all), even while the editor panel has one. "This and following" on the first occurrence acts as "all". Dragging from the all-day lane into the grid makes a 1-hour timed event at the drop slot; dragging into the all-day lane makes an all-day event on that date. Moving an occurrence onto or past the previous or next occurrence is refused before saving, with a message.
- R8. Moving an event to a different calendar within the **same** account is supported. Moving across accounts is a copy + delete with confirmation, queued as one linked outbox group (the delete is not sent if the copy fails).
- R9. Read-only calendars and meetings where you are an attendee cannot be dragged or edited; this is shown visually. Your own reminder on an attendee meeting stays editable ([attendees-invitations](attendees-invitations.md) R7).
- R10. Delete: single events/occurrences are deleted immediately with an undo banner; whole-series deletes ask for confirmation; recurring deletes ask for the scope (see [undo](undo.md) R7). Meetings with attendees ask first ([attendees-invitations](attendees-invitations.md) R7–R8).
- R11. Keyboard: full keyboard operation per [keyboard-navigation](keyboard-navigation.md).
- R12. Performance: switching views or periods renders in < 200 ms from the local store, for up to 2,000 occurrences in the window.

## Out of scope

- Year view, custom N-day views and schedule views (v1). Agenda is covered by [agenda-view](agenda-view.md).
- Rich-text/HTML descriptions (HTML descriptions from Graph are displayed as plain text and are read-only in v1, R6).
- Attendee/invite editing, and responding to invitations (done in Outlook via Open in Outlook, [attendees-invitations](attendees-invitations.md)).
- Selecting several events at once.
- Printing.
- .ics file import/export and ICS URL subscriptions (subscriptions are handled in Radicale).

## Acceptance criteria

- [ ] All views render events from both MS365 and Radicale in the correct calendar colours.
- [ ] A calendar colour or shape changed in the app persists across restarts and is not changed on the server.
- [ ] Each event card shows its calendar's shape marker before the title.
- [ ] A single click opens the editor panel; a small drag moves the event instead.
- [ ] Changing an event's title and clicking another event saves the change with no prompt, and Ctrl+Z reverts it.
- [ ] Drag-move and resize update the server after sync.
- [ ] Dragging a recurring occurrence prompts for the scope, and each choice behaves as specified.
- [ ] Views work fully offline.

## Dependencies

- [local-store-offline](local-store-offline.md), [recurring-events](recurring-events.md), [timezone-handling](timezone-handling.md), [event-location](event-location.md), [reminders-notifications](reminders-notifications.md)
- [agenda-view](agenda-view.md), [event-show-as](event-show-as.md), [online-meeting-join](online-meeting-join.md), [quick-add](quick-add.md), [undo](undo.md), [keyboard-navigation](keyboard-navigation.md), [calendar-shading](calendar-shading.md), [calendar-sets](calendar-sets.md), [duplicate-event](duplicate-event.md), [ui-colour-theme](ui-colour-theme.md), [display-scaling](display-scaling.md), [attendees-invitations](attendees-invitations.md)
