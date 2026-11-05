---
description: Local SQLite store, offline read/write and the outbox that pushes local changes when online
when-to-use: Before working on the data model, persistence, offline behaviour, or the change queue
tags: [storage, sqlite, offline, outbox, data-model]
status: in-progress
---

# Local store and offline

## Summary

All synced data is held in a local SQLite database, so the app is fully usable offline. Local creates, edits and deletes apply to the local store immediately and are recorded in an outbox. The sync engine drains the outbox when connectivity returns.

## Requirements

- R1. SQLite database in the app's LocalState folder (Microsoft.Data.Sqlite). Schema versioned with forward migrations; a store newer than the build is not opened ([app-updates](app-updates.md) R4).
  - Code lives in the `WindowsCalendar.Core` library (no WinUI), unit-tested in `WindowsCalendar.Core.Tests`. Version kept in `PRAGMA user_version`; each migration runs in its own transaction. WAL journal, foreign keys on. Times stored as UTC Unix milliseconds.
  - Interim (until [ui-states](ui-states.md)): a too-new store is logged and not opened; the on-screen message comes later.
- R2. Item storage keeps both:
  - the **raw server payload** (iCalendar text for CalDAV; Graph JSON for MS365), to avoid lossy round-trips, and
  - **normalised columns** for querying (collection, UID / Graph id, summary, start/end UTC, all-day, tz, location, status, due, completed, recurrence flags, etag / changeKey, lastModified).
  - A local delete keeps the row as a hidden tombstone until the server delete succeeds (the delete needs the remote id and etag). An item created and deleted before it ever synced is removed outright.
- R3. An occurrence index (expanded instances inside the sync window) for fast view rendering. It is regenerated when a recurring item changes, and when the Windows timezone changes (floating-time and all-day items depend on it, [timezone-handling](timezone-handling.md) R6).
- R4. Outbox table: `{ id, itemRef, operation: create|update|delete, payload, baseEtag, createdAt, attempts, lastError }`. Operations on the same item are coalesced (e.g. create+update → create; create+delete → drop). Entries replay in FIFO order across items; linked groups replay in their own order.
  - Coalescing applies only to the item's latest entry, and only when it is not in a linked group and not in flight. Rules: create+update → create (new payload); create+delete → no entry, item removed; update+update → update (first base etag kept); update+delete → delete.
  - The sync engine marks an entry in flight before sending it; a later local change then gets its own entry. In-flight marks are cleared when the store opens (crash recovery).
  - A coalesced entry keeps its original queue position.
- R5. Every UI write goes to the local store + outbox in one transaction. The UI never waits for the network.
- R6. Connectivity detection (NetworkInformation) triggers an outbox flush when the network returns. An account counts as online only once its server answers, so a captive portal shows as Offline ([ui-states](ui-states.md) R2).
- R7. Failed outbox entries back off exponentially, capped at 15 minutes; Sync now resets the backoff. Permanent failures (4xx other than 401/412 and the consent-revoked 403 of [sync-ms365](sync-ms365.md) R11; 409 counts as permanent) are shown to the user with options to retry or discard; a discarded entry is moved to Conflicts ([conflict-resolution](conflict-resolution.md) R9). Entries for an account in "auth required" are held until sign-in succeeds.
- R8. Sync state per collection: Graph deltaLink(s) per window chunk; CalDAV sync-token and CTag.
- R9. Items pending upload are visibly marked (e.g. a small sync glyph).
- R9a. Pending changes list: clicking the "n changes waiting" status ([ui-states](ui-states.md) R3) opens a list of outbox entries showing the item, the operation, and its state (waiting, or failed with the reason), with Retry and Discard on failed entries.
- R10. Sync scheduling: every 5 minutes; immediately after a local change; on app focus; on network reconnect; plus a manual "Sync now" button (UI toolbar and tray).

## Out of scope

- Encrypting the SQLite file itself (it holds calendar data, not credentials; BitLocker/OS-level protection assumed). Revisit if needed.
- Multi-device sync of the local store.

## Acceptance criteria

- [ ] With network disabled: create, edit and delete events and tasks; restart the app; changes persist.
- [ ] Re-enabling the network pushes all queued changes and the server reflects them.
- [ ] Coalescing: create then delete offline produces no server call.
- [ ] Manual "Sync now" triggers an immediate sync for all accounts.

## Dependencies

- [app-platform](app-platform.md), [account-management](account-management.md)
- Consumed by [sync-ms365](sync-ms365.md), [sync-radicale](sync-radicale.md), [conflict-resolution](conflict-resolution.md)
