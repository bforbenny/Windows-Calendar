---
description: Undo (Ctrl+Z) for create, edit, move/resize, delete, duplicate and task completion, built on the outbox
when-to-use: Before working on undo, delete flows, or any user action that changes an item
tags: [ui, undo, outbox, offline]
status: closed
---

# Undo

## Summary

The user can undo recent changes with Ctrl+Z or from the "Undo" banner that appears after each change. An undo that runs before the change has synced simply cancels the queued operation. Once the change has synced, undo queues the reverse operation.

## Requirements

- R1. Undoable actions: create, edit (any field), move/resize (drag or keyboard), delete, duplicate, task complete/uncomplete, move to another calendar. Not undoable: cancelling a meeting you organise ([attendees-invitations](attendees-invitations.md) R8). Undoing a change that sent an update to attendees sends another update.
- R2. Undo stack: per app session, last 20 actions, in memory only (cleared on quit).
- R3. After each undoable action, an in-app banner shows "Event deleted · Undo" (or similar) for 8 seconds. Edits made in the editor panel show no banner (Ctrl+Z still undoes them). Ctrl+Z undoes the latest action while the main window has focus, except while a text box has focus, where it undoes typing ([calendar-views](calendar-views.md) R6d). The [tray-flyout](tray-flyout.md) has no undo bar; its actions go on the main window's undo stack (tray-flyout R4b).
- R4. Mechanics:
  - If the action's outbox entry is not yet sent, undo removes/reverts it (coalesced per [local-store-offline](local-store-offline.md)).
  - If already synced, undo enqueues the inverse: edit → write previous values; create → delete; delete → re-create from the stored previous payload (same UID on CalDAV; new id on Graph).
- R5. Recurring edits undo the whole scoped action, including a "this and following" split (delete the new series and restore the original rule), as one linked outbox group ([recurring-events](recurring-events.md)). The undo payload includes the overrides the split dropped from the original series, and restores them. If they cannot be restored (e.g. Graph regenerated exception ids), undo is refused with a message and nothing is changed.
- R6. If the server version changed after the action (ETag differs on the next sync), undo is refused with "This item changed on the server; can't undo". Nothing is overwritten.
- R7. Delete confirmation (changed 2026-11-12): every delete still asks for confirmation, and then shows the undo banner ("Event deleted · Undo", "Series deleted · Undo", "Task deleted · Undo"). Meetings with attendees always ask and get no banner ([attendees-invitations](attendees-invitations.md) R7–R8).
- R8. Undo for a Graph item deleted on the server and re-created gets a new id. Re-created events lose attendee/meeting data that the app cannot write. The banner text warns when this applies.

## Interim (first slice: drag moves)

- Only drag moves and resizes ([calendar-views](calendar-views.md) R7), including recurring ones of any scope, deleting one occurrence of a series, and "this and following" splits (R5: the new series is deleted and the original put back, one linked group; refused if either changed on the server) are undoable so far, one level deep: the banner ("Event moved · Undo", "Occurrence deleted · Undo") for 8 seconds, and Ctrl+Z for the latest action while no text box has focus.
- The banner (R3) floats per [floating-banners](floating-banners.md).
- R4 and R6 apply: undo writes the previous version of the event back (coalescing with an unsent change, otherwise queuing an update), and is refused if the item changed on the server since the action.
- Duplicate ([duplicate-event](duplicate-event.md)) is undoable the same way: "Event duplicated · Undo" deletes the copy.
- Task complete / reopen by its checkbox (Tasks view, calendar, tray) is undoable the same way: "Task completed · Undo" / "Task reopened · Undo" ([tasks](tasks.md) second slice).

## Interim (second slice: full stack, added 2026-11-12)

- R2: a 20-step stack for the session, in memory. Ctrl+Z walks back one step at a time while no text box has focus.
- Capture: the store records the before and after of every local create, update, delete, move and split made inside an undo scope. One user action is one step, so a split, a move (create + delete) or a "Doesn't repeat" change undoes as a whole. Server-applied changes are never recorded.
- R1 covered:
  - creates (editor, quick add, duplicate);
  - every editor field change, including the reminder and the repeat rule;
  - drag moves and resizes;
  - deletes of events, occurrences, following events, series and tasks;
  - task ticks;
  - moves to another calendar or task list, also across accounts;
  - conflict "Use mine".
- Not undoable: "Delete completed tasks older than 1 year" ([tasks](tasks.md) R10).
- R3: banner for drags, duplicates, quick adds, ticks, deletes and moves to another calendar or list. Editor field and rule changes have no banner. A new step closes a banner still offering Undo for an older one.
- R4: an unsent change is reverted in the outbox. An unsent delete is cancelled, and an update it had absorbed is put back. A sent delete re-creates the item: CalDAV keeps the same UID, MS365 gets a new id.
- Undo of a create deletes the item without the server-change check, as duplicate does: an MS365 create always comes back changed after its first sync.
- R5: a CalDAV split restores the original series and its overrides and deletes the new series, as one linked group. MS365 changes that made Exchange drop changed occurrences (a split, or a series time or rule change) are refused: "Changed occurrences that were dropped can't be restored, so this change can't be undone."
- R6: "This item changed on the server; can't undo". A delete still being sent: "Can't undo yet · This change is being sent; try again in a moment" (the step stays).
- R7: as changed 2026-11-12. Every delete asks first, then shows the banner.
- R8: the warning shows after the undo, when it is known whether the item was re-created. "Event restored · It was already deleted on the server, so it was added again as new: attendees and online meeting details are not restored." An MS365 occurrence whose delete was already sent can't be re-created: "This occurrence was already deleted on the server, so it can't be restored."

## Out of scope

- Redo.
- Undo history across app restarts.
- Undo of account add/remove, settings changes, or conflict resolution choices.

## Acceptance criteria

- [x] Drag an event, press Ctrl+Z: it returns to its original time, online and offline.
- [x] Delete an event, click Undo after sync: it is restored on the server.
- [x] Undo of a "this and following" split restores a single series on both backends.
- [x] Undo after a server-side change is refused without data loss.

## Dependencies

- [local-store-offline](local-store-offline.md), [recurring-events](recurring-events.md), [conflict-resolution](conflict-resolution.md), [calendar-views](calendar-views.md)
