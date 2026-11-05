---
description: Undo (Ctrl+Z) for create, edit, move/resize, delete, duplicate and task completion, built on the outbox
when-to-use: Before working on undo, delete flows, or any user action that changes an item
tags: [ui, undo, outbox, offline]
status: pending
---

# Undo

## Summary

The user can undo recent changes with Ctrl+Z or from the "Undo" banner that appears after each change. An undo that runs before the change has synced simply cancels the queued operation. Once the change has synced, undo queues the reverse operation.

## Requirements

- R1. Undoable actions: create, edit (any field), move/resize (drag or keyboard), delete, duplicate, task complete/uncomplete, move to another calendar. Not undoable: cancelling a meeting you organise ([attendees-invitations](attendees-invitations.md) R8). Undoing a change that sent an update to attendees sends another update.
- R2. Undo stack: per app session, last 20 actions, in memory only (cleared on quit).
- R3. After each undoable action, an in-app banner shows "Event deleted · Undo" (or similar) for 8 seconds. Edits made in the editor panel show no banner (Ctrl+Z still undoes them). Ctrl+Z undoes the latest action while the main window has focus, except while a text box has focus, where it undoes typing ([calendar-views](calendar-views.md) R6d). The [tray-flyout](tray-flyout.md) shows the same banner for task completions made in it.
- R4. Mechanics:
  - If the action's outbox entry is not yet sent, undo removes/reverts it (coalesced per [local-store-offline](local-store-offline.md)).
  - If already synced, undo enqueues the inverse: edit → write previous values; create → delete; delete → re-create from the stored previous payload (same UID on CalDAV; new id on Graph).
- R5. Recurring edits undo the whole scoped action, including a "this and following" split (delete the new series and restore the original rule), as one linked outbox group ([recurring-events](recurring-events.md)). The undo payload includes the overrides the split dropped from the original series, and restores them. If they cannot be restored (e.g. Graph regenerated exception ids), undo is refused with a message and nothing is changed.
- R6. If the server version changed after the action (ETag differs on the next sync), undo is refused with "This item changed on the server; can't undo". Nothing is overwritten.
- R7. Delete confirmation: deleting a single event or occurrence uses the undo banner instead of a confirmation dialog. Deleting a whole recurring series still asks for confirmation. Meetings with attendees always ask and get no banner ([attendees-invitations](attendees-invitations.md) R7–R8).
- R8. Undo for a Graph item deleted on the server and re-created gets a new id. Re-created events lose attendee/meeting data that the app cannot write. The banner text warns when this applies.

## Out of scope

- Redo.
- Undo history across app restarts.
- Undo of account add/remove, settings changes, or conflict resolution choices.

## Acceptance criteria

- [ ] Drag an event, press Ctrl+Z: it returns to its original time, online and offline.
- [ ] Delete an event, click Undo after sync: it is restored on the server.
- [ ] Undo of a "this and following" split restores a single series on both backends.
- [ ] Undo after a server-side change is refused without data loss.

## Dependencies

- [local-store-offline](local-store-offline.md), [recurring-events](recurring-events.md), [conflict-resolution](conflict-resolution.md), [calendar-views](calendar-views.md)
