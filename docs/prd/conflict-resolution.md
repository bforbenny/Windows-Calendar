---
description: Policy for items changed both locally and on the server — server wins, local version kept as a recoverable copy
when-to-use: Before working on sync write paths, 412 handling, or the conflicts UI
tags: [sync, conflicts, offline]
status: pending
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

## Out of scope

- Field-level automatic merging.
- Last-write-wins by timestamp.

## Acceptance criteria

- [ ] Edit an event offline, edit the same event on the server, reconnect → the server version is shown, a conflict copy exists, and the user is notified.
- [ ] "Use my version" pushes the local edit successfully.
- [ ] Server delete + local edit → the item is gone, and the edit is recoverable from the Conflicts view.

## Dependencies

- [local-store-offline](local-store-offline.md), [sync-ms365](sync-ms365.md), [sync-radicale](sync-radicale.md)
