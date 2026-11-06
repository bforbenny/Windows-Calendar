---
description: Week, Month (and Agenda) calendar views with navigation, calendar colours, event create/edit, and drag to move/resize
when-to-use: Before working on calendar UI, view rendering, or the event editor
tags: [ui, views, events, drag-drop]
status: in-progress
---

# Calendar views

## Summary

The main UI provides Week and Month views (plus Agenda) of all visible calendars across accounts. Events are colour-coded by calendar. Events can be created, opened, edited, moved and resized directly in the views.

## Requirements

- R1. Views: Week (configurable first day of week, default from Windows locale), Month, Agenda (see [agenda-view](agenda-view.md)) and Tasks (see [tasks](tasks.md)). There is no Day view; where other PRDs say "Day/Week", read "Week". Toolbar with Today, view switcher, period title, quick add, Sync now, and sync status.
- R1a. Natural scrolling, no Previous/Next: views scroll freely across the local store's occurrence window (-3 / +12 months, [local-store-offline](local-store-offline.md) R3).
  - Week: seven day columns fill the width; scrolls horizontally through days and vertically through hours. Day headers and the all-day lane follow the horizontal scroll; hour labels follow the vertical scroll. Opens on the current week.
  - Month: about five weeks fill the height; scrolls vertically through weeks. Opens on the current week.
  - The period title shows the month at the scroll position. Today scrolls back to today. Only the days near the viewport are built.
  - In Month, "+n more" opens Agenda with that day as the first column.
- R2. A sidebar contains:
  - the calendar set selector ([calendar-sets](calendar-sets.md));
  - two sections, Calendars and Task lists, each grouped by account; every calendar and task list has a visibility checkbox and colour swatch (its shape marker); the checkbox is the only per-calendar visibility control and works as an override of the active set ([calendar-sets](calendar-sets.md) R5);
  - a mini month navigator.
- R2b. Sidebar layout:
  - a hamburger button at the far left of the toolbar (and Ctrl+B, [keyboard-navigation](keyboard-navigation.md)) hides the sidebar fully, so the views take the whole width, and shows it again;
  - the Calendars and Task lists section headings, and each account group within them, have a chevron to collapse and expand them;
  - the sidebar's shown/hidden state and every collapsed section and account group persist across restarts (stored locally in the store's `meta` table);
  - a collection's status follows its name as an icon in the secondary text colour, with a tooltip and screen-reader name giving the text: link icon for a subscribed feed, lock icon for a read-only calendar, warning icon for a feed that is not secure and so not synced;
  - unticked calendars and task lists are not pulled from the server; ticking one syncs it straight away ([local-store-offline](local-store-offline.md) R10);
  - while a calendar or task list is syncing, its name shows a hue wave moving left to right ([ui-states](ui-states.md) R1); with Windows animations off, the name is shown in the accent colour instead.
- R2a. Calendar colours and shapes, per [ui-colour-theme](ui-colour-theme.md) R6–R9:
  - each calendar or task list has one of 24 colours (8 hues × 3 levels) and a shape marker (up triangle, down triangle, square, circle, pentagon), shown by default;
  - the initial colour is the server colour (Graph `hexColor` / `color`; CalDAV `calendar-color`) snapped to the nearest of the 24, or else an automatic assignment;
  - the user can override the colour and the shape (or a Fluent icon, [ui-colour-theme](ui-colour-theme.md) R7) per calendar or task list;
  - overrides are stored locally and not written back to the server;
  - event cards use the fill, bar and text steps defined in [ui-colour-theme](ui-colour-theme.md), which meet WCAG AA in both themes.
- R3. All-day / multi-day events are shown in an all-day lane in Day/Week, and as bars in Month.
- R4. Overlapping events in Day/Week are laid out side by side.
- R4a. Short events in Day/Week are drawn at their true height. Below 24 epx tall they show one line with the title only; below 12 epx (under about 11 minutes) only the coloured bar and fill. From 24 epx the time follows the title on one line; from 44 epx the time and location go on a second line. The full title and time appear in a tooltip on hover or keyboard focus, and the event's accessible name is always set for screen readers.
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
- R7. Drag to move and drag the top or bottom edge to resize in Day/Week, snapping to 15 minutes (minimum length 15 minutes). Drag to move between days in Month and Agenda, keeping the time and length. A preview shows where the event will land; Esc cancels the drag; holding near the edge of the view scrolls it. For recurring events, a drag always asks for the scope (this / this and following / all), even while the editor panel has one. "This and following" on the first occurrence acts as "all". Dragging from the all-day lane into the grid makes a 1-hour timed event at the drop slot; dragging into the all-day lane makes an all-day event on that date. Moving an occurrence onto or past the previous or next occurrence is refused before saving, with a message.
- R8. Moving an event to a different calendar within the **same** account is supported. Moving across accounts is a copy + delete with confirmation, queued as one linked outbox group (the delete is not sent if the copy fails). Between Microsoft 365 and CalDAV the fields are mapped in full, and the confirmation lists what cannot be carried over. Meetings and invitations (events with attendees or an organiser) cannot be moved to another calendar.
- R9. Read-only calendars and meetings where you are an attendee cannot be dragged or edited; this is shown visually. Your own reminder on an attendee meeting stays editable ([attendees-invitations](attendees-invitations.md) R7).
- R10. Delete: single events/occurrences are deleted immediately with an undo banner; whole-series deletes ask for confirmation; recurring deletes ask for the scope (see [undo](undo.md) R7). Meetings with attendees ask first ([attendees-invitations](attendees-invitations.md) R7–R8).
- R11. Keyboard: full keyboard operation per [keyboard-navigation](keyboard-navigation.md).
- R12. Performance: switching views or periods renders in < 200 ms from the local store, for up to 2,000 occurrences in the window.

## Interim (first slice: read-only views)

- Week, Month and Agenda with the toolbar (Today, view switcher, period title, Sync now). No editor, create, drag or delete yet.
- The week starts on the Windows first day of week.
- Sidebar: checkbox, swatch and shape per calendar and task list. Hidden collections are remembered in the store's `meta` table until [calendar-sets](calendar-sets.md) takes over.
- Colours: server colours are snapped per [ui-colour-theme](ui-colour-theme.md) interim, otherwise automatic; overrides come later.
- R1: the Tasks tab follows Agenda; open tasks with a due date show in Week, Month and Agenda per [tasks](tasks.md) interim.
- Layout maths (overlap columns, periods) lives in `WindowsCalendar.Core` with tests; render time is logged for R12.

## Interim (second slice: editor)

- R6, R6b (click), R6c and R6d with these fields: title, all-day, start and end, repeat ([recurring-events](recurring-events.md) R3 interim), location, description (plain text), calendar (writable calendars in every account) and Delete. Other properties stay untouched in the payload ([sync-radicale](sync-radicale.md) R8).
- Times are edited in the Windows timezone and written in the event's own timezone; floating and UTC times stay so. New events use the Windows timezone with a VTIMEZONE.
- Read-only calendars open view-only with a notice. Recurring events are editable per the [recurring-events](recurring-events.md) interim (this / this and following / all).
- R5 interim: clicking an empty Week slot opens the editor on a new 1-hour event at that half hour; the all-day lane, a Month day or the empty part of an Agenda day gives an all-day event. The quick-create popup comes with [quick-add](quick-add.md).
- The toolbar has a "New event" split button: it opens the editor on a new 1-hour event at the next full hour, on today when today is in view, otherwise on the day in the middle of the view. Its drop-down offers New event and New task ([tasks](tasks.md) interim). In the Tasks view the button reads "New task" and opens a new task.
- R8 within an account: the move is queued as a create in the new calendar then a delete of the old one, one linked outbox group. An unsent new event is re-targeted instead.
- Calendar field: each choice shows its source after the name, muted: the server host for Radicale (e.g. "Personal  dav-1.bforbenny.com"), the account email for MS365. The task editor's list line shows the same.
- R8 across accounts: picking another account's calendar saves typed changes first, then asks "Move to <calendar>?" with what the event can't take along. Move queues the create there and the delete here as one linked group; the editor then shows the moved event. Cancel keeps the event where it is. Not undoable yet.
  - Same kind of account: CalDAV copies the stored event whole (same UID, new href). MS365 copies the fields a create may write.
  - CalDAV to MS365: title, times in the event's own timezone, all-day, location with GEO as coordinates, description, repeat rule (any the editor can show), the first reminder before the start, show-as (X-MICROSOFT-CDO-BUSYSTATUS, else TRANSP), CLASS as sensitivity, PRIORITY as importance, CATEGORIES. Listed as lost: other reminders, changed occurrences, deleted occurrences (they come back). A repeat rule the editor can't show is refused.
  - MS365 to CalDAV: the same fields the other way, with an HTML description as plain text (its formatting listed as lost), the reminder as a display VALARM, show-as as TRANSP plus X-MICROSOFT-CDO-BUSYSTATUS for tentative, out of office and working elsewhere.
  - Refused: meetings and invitations, in any account and also within one account (the Calendar field is disabled for them); repeating MS365 events (as within an account).
- R10 interim: Delete asks for confirmation until [undo](undo.md) exists. No keyboard selection yet. For a recurring event Delete asks for the scope: one occurrence is deleted at once with the undo banner, the entire series asks for confirmation.

### Pending user check (R8 across accounts)

Built on branch `duplicate-event` (2026-11-06), not yet verified in the app or merged:

- [x] Move a repeating Radicale event to MS365: the confirmation lists what is lost; the series appears in Outlook.
- [ ] Move an MS365 event with a reminder and "Out of office" to Radicale: both carry over.
- [x] A meeting or invitation opens with the Calendar field disabled.

## Interim (third slice: drag)

- R7 with mouse and pen. On touch, a swipe keeps scrolling; press-and-hold to drag comes later.
- Read-only calendars and subscriptions cannot be dragged. Recurring occurrences can; the drop asks for the scope (this / this and following / all, [recurring-events](recurring-events.md) interim).
- A drop is saved like an editor change and gets the "Event moved · Undo" banner ([undo](undo.md) interim).

## Out of scope

- Year view, custom N-day views and schedule views (v1). Agenda is covered by [agenda-view](agenda-view.md).
- Rich-text/HTML descriptions (HTML descriptions from Graph are displayed as plain text and are read-only in v1, R6).
- Attendee/invite editing, and responding to invitations (done in Outlook via Open in Outlook, [attendees-invitations](attendees-invitations.md)).
- Selecting several events at once.
- Printing.
- .ics file import/export and adding ICS URL subscriptions in the app (subscriptions are added in Radicale; the app fetches their feeds, [sync-radicale](sync-radicale.md) R1a).

## Acceptance criteria

- [ ] All views render events from both MS365 and Radicale in the correct calendar colours.
- [ ] The hamburger button and Ctrl+B hide and show the sidebar; the state survives a restart.
- [ ] Collapsing the Task lists section and one account group survives a restart.
- [ ] Subscribed, read-only and not-secure collections show a link, lock or warning icon instead of a text label, with the text as a tooltip.
- [ ] During Sync now, each calendar's name shows the hue wave only while that calendar syncs.
- [ ] Sync now skips unticked calendars (no wave, no pull in the log); ticking one waves and syncs only that calendar.
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
