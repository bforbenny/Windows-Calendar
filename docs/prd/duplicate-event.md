---
description: Duplicate an event (menu, Ctrl+D or Ctrl+drag) into the same or another writable calendar
when-to-use: Before working on duplicate/copy of events
tags: [events, duplicate, ui]
status: pending
---

# Duplicate event

## Summary

The user can copy an existing event into a new, independent event and then adjust it. This is quicker than retyping a similar event.

## Requirements

- R1. Triggers: context menu "Duplicate", Ctrl+D on the selected event, and Ctrl+drag in Day/Week/Month (drop location sets the new start).
- R2. Menu/Ctrl+D creates the copy immediately at the same time in the same calendar and opens it in the editor panel (auto-save, [calendar-views](calendar-views.md) R6d), so the user can change the time or calendar. Ctrl+Z or the undo banner removes the copy.
- R3. Copied fields: title, start/end, all-day, timezone, location (including preserved coordinates), description, show-as, reminders.
- R4. Not copied: server ids/UID/ETag, attendees and organiser, the Graph online meeting / join URL, conflict copies.
- R5. Duplicating an occurrence of a recurring event creates a single non-recurring event. Duplicating from the series editor copies the recurrence rule too.
- R6. Target calendar can be any writable calendar, including one in another account. In that case, unsupported fields are dropped (e.g. show-as values per [event-show-as](event-show-as.md) R2).
- R7. Duplicate works offline (outbox create) and is undoable ([undo](undo.md)).

## Out of scope

- Duplicating tasks.
- Event templates.

## Acceptance criteria

- [ ] Ctrl+D on an event creates an identical copy, opens it in the editor panel, and after sync a second event exists on the server.
- [ ] Ctrl+drag of an event to another day creates a copy there and leaves the original unchanged.
- [ ] A duplicated Teams meeting has no Join link and no attendees.

## Dependencies

- [calendar-views](calendar-views.md), [local-store-offline](local-store-offline.md), [recurring-events](recurring-events.md), [undo](undo.md)
