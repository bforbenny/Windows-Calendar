---
description: Policy for items changed both locally and on the server — server wins, local version kept as a recoverable copy
when-to-use: Before working on sync write paths, 412 handling, or the conflicts UI
tags: [sync, conflicts, offline]
status: in-progress
---

# Conflict resolution

## Summary

When a local pending change and a server change hit the same item, **the server version wins**. The local version is kept as a recoverable conflict copy, and the user is notified. This never silently loses an edit and keeps the sync logic simple.

## Requirements

- R1. Detection: a write returns 412 (etag/changeKey mismatch), or an incoming sync change arrives for an item with a pending outbox entry.
- R2. Resolution: apply the server version to the item, and move the local pending version into a `Conflicts` table `{ itemRef, localPayload, serverPayload, detectedAt }`. Remove the outbox entry.
- R3. Server-side delete vs local edit: the item is deleted locally, and the local edit is kept in Conflicts (it can be restored as a new item).
- R4. Local delete vs server edit: the server version is restored locally; the user is notified that the delete was not applied.
- R5. Notification: an in-app banner/badge "N conflicts", plus a single toast per sync cycle (not per item).
- R6. Conflicts view: list conflicts showing local vs server field differences, with actions **Keep server** (discard the copy) or **Use my version** (re-queue the local payload against the new etag). For a CalDAV series, Use my version first lists the server changes it would overwrite (fields and occurrences that differ from the version the local edit was based on) and asks for confirmation.
- R7. Conflict copies are never auto-deleted. The user clears them.
- R8. For recurring series, conflicts are tracked at the granularity the server uses (Graph: per occurrence/series master; CalDAV: per resource, i.e. the whole series).
- R9. Other sources of copies: a failed outbox entry the user discards ([local-store-offline](local-store-offline.md) R7) and pending changes in a collection deleted on the server ([account-management](account-management.md) R11) are also moved to Conflicts, so they stay recoverable.
- R10. Edits to a meeting you organise can conflict only because attendee responses changed the etag. This is accepted in v1 and handled like any other conflict.
- R11. The app's own writes never count as conflicts: a server version that is the echo of a write the app sent (including a create followed by a still-queued edit, as after a move to another account) is not a conflict. Covered by a unit test that creates, edits before the push, then syncs.

## Interim (first slice, with sync-radicale push)

- R1–R4, R7 and R8 (CalDAV, per resource) are in place. The `conflicts` table holds `{ id, item_id, collection_id, kind, local_payload, server_payload, detected_at }`; a null local payload means the local change was a delete, a null server payload means the server deleted the item.
- R5: an "N conflicts" banner in the main window, closable for the session. The toast comes with [reminders-notifications](reminders-notifications.md) toasts.
  - Clicking the banner, or its Show button (keyboard), opens a list just above it, with the first openable row focused (arrows move, Enter opens, Esc closes). The list holds every conflict, newest first, even when there is only one. Each row shows the type icon, title, date and time, calendar marker and name, and what happened: "Changed on both" (both versions exist), "Your delete was not applied" (local delete, server edit), or "No longer on the calendar" (deleted on the server, or a create cancelled with its group).
  - Clicking a row whose item is on the calendar closes the list, scrolls to its day and opens it in the editor, like a search result ([search](search.md) R5). A row whose item is gone is greyed and not clickable; its title comes from the saved local version.
  - Opening the list or an item does not clear the conflict (R7); the banner stays until closed.
- R6 (second slice): resolving happens in that list, not a separate view:
  - each row shows one line of differences between the saved local version and the item as it is now, for the fields the app edits (title, start, end, all-day, location, show-as, reminder), e.g. "End: yours 17:30 · server 11:30"; for a Graph local version only the fields it marked as changed are compared;
  - **Keep server** removes the conflict copy; **Use mine** applies the local version to the item as it is now and queues it as a normal edit against the current etag/changeKey, then removes the copy; for "Your delete was not applied" it queues the delete again. Both are one undo step where the outbox allows it ([undo](undo.md));
  - for a row whose item is no longer on the calendar, only **Keep server** (discard) is offered; restoring it as a new item (R3) comes later;
  - the banner count updates at once, and the banner closes when no conflicts are left.
  - The "Use my version" confirmation for CalDAV series that lists overwritten server changes (R6) comes later; until then Use mine on a CalDAV series asks a plain "Overwrite the server version?" first.- R6 (Conflicts view) comes later. R9 for discarded outbox entries is built with [ui-states](ui-states.md) slice 1 ([local-store-offline](local-store-offline.md) R7); R9 for collections deleted on the server comes later.
- A conflict on an entry in a linked outbox group cancels the group's later entries: a create is not sent (its local copy goes to Conflicts and it leaves the calendar), a delete is not sent (the item comes back). So a failed series truncate does not create the new series ([recurring-events](recurring-events.md) R6), and a failed calendar move does not delete the original ([calendar-views](calendar-views.md) R8).

## Out of scope

- Field-level automatic merging.
- Last-write-wins by timestamp.

## Acceptance criteria

- [ ] Edit an event offline, edit the same event on the server, reconnect → the server version is shown, a conflict copy exists, and the user is notified.
- [ ] "Use my version" pushes the local edit successfully.
- [ ] Server delete + local edit → the item is gone, and the edit is recoverable from the Conflicts view.

## Dependencies

- [local-store-offline](local-store-offline.md), [sync-ms365](sync-ms365.md), [sync-radicale](sync-radicale.md)
