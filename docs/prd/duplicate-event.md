---
description: Duplicate an event (menu, Ctrl+D or Ctrl+drag) into the same or another writable calendar
when-to-use: Before working on duplicate/copy of events
tags: [events, duplicate, ui]
status: closed
---

# Duplicate event

## Summary

The user can copy an existing event into a new, independent event and then adjust it. This is quicker than retyping a similar event.

## Requirements

- R1. Triggers: context menu "Duplicate", Ctrl+D on the selected event, and Ctrl+drag in Day/Week/Month (drop location sets the new start).
- R2. Menu/Ctrl+D creates the copy immediately at the same time in the same calendar and opens it in the editor panel (auto-save, [calendar-views](calendar-views.md) R6d), so the user can change the time or calendar. Ctrl+Z or the undo banner removes the copy.
- R3. Copied fields: title, start/end, all-day, timezone, location (including preserved coordinates), description, show-as, reminders.
- R4. Not copied: server ids/UID/ETag, attendees and organiser, the Graph online meeting / join URL, conflict copies.
- R5. Duplicating an occurrence of a recurring event creates a single non-recurring event.
- R6. The copy goes into the same calendar as the original; it can then be moved to any writable calendar with the editor's Calendar field ([calendar-views](calendar-views.md) R8).
- R7. Duplicate works offline (outbox create) and is undoable ([undo](undo.md)).

## Interim (first slice)

- Built: R1, R2, R3, R4, R5 for occurrences, and R7. The card's right-click menu has Duplicate (Ctrl+D shown). Ctrl+D duplicates the selected event. Ctrl+drag in Week, Month and Agenda copies to the drop, without opening the editor. The copy is selected.
- Any event in a writable calendar can be duplicated, including meetings and invitations that cannot be edited. Tasks, read-only and subscribed calendars cannot.
- R3/R4 on CalDAV: the VEVENT is copied with a new UID and href, and SEQUENCE 0. ATTENDEE, ORGANIZER and CONFERENCE are dropped. Alarms, TRANSP, GEO and other properties stay. The TZID form stays. An occurrence copies its override when it has one.
- R3/R4 on MS365: only the fields a create may write are copied (subject, body, times, all-day, location incl. coordinates, show-as, sensitivity, importance, categories, reminder). Times are written in the event's own timezone. A stub occurrence takes its master's fields. A repeating event still syncing is refused with a message.
- Undo (R7) deletes the copy: an unsent create is dropped from the outbox, a sent one is deleted on the server. Edits made to the copy since are deleted with it, and no server-change check is made.
- Dropped 2026-11-12 (see Out of scope): R5 "from the series editor" (the editor has no Duplicate yet) and R6 into another calendar (the copy goes into the same calendar; it can then be moved to any calendar from the editor's Calendar field, per [calendar-views](calendar-views.md) R8; a copy of a meeting has no attendees, so it can move).

## Pending user check

Built on branch `duplicate-event` (2026-11-06); verified and merged into main.

- [x] Ctrl+D on a Teams meeting: the copy has no attendees or Join link and appears in Outlook after sync.
- [x] Right-click Duplicate opens the copy in the editor; the Undo banner removes it.
- [x] Ctrl+drag to another day creates a copy there and leaves the original unchanged.

## Out of scope

- Duplicating tasks.
- Event templates.
- Dropped 2026-11-12: Duplicate from the series editor (R5, copying the rule) and duplicating straight into another calendar (R6). The copy goes into the same calendar and can be moved with the editor's Calendar field.

## Acceptance criteria

- [x] Ctrl+D on an event creates an identical copy, opens it in the editor panel, and after sync a second event exists on the server.
- [x] Ctrl+drag of an event to another day creates a copy there and leaves the original unchanged.
- [x] A duplicated Teams meeting has no Join link and no attendees.

## Dependencies

- [calendar-views](calendar-views.md), [local-store-offline](local-store-offline.md), [recurring-events](recurring-events.md), [undo](undo.md)
