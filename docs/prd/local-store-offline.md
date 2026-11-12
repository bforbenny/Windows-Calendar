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
  - Table `occurrences` (item, start, end) covering -3 / +12 months from today. CalDAV recurring items are expanded with Ical.Net 5 (overrides and cancelled occurrences applied); MS365 occurrences and non-recurring items get one row from their own start/end. Floating and all-day times use the Windows timezone.
  - An item's rows are rebuilt on every write. The whole index is rebuilt daily (rolling window) and when the Windows timezone changes (checked on window focus and every scheduled sync tick).
- R4. Outbox table: `{ id, itemRef, operation: create|update|delete, payload, baseEtag, createdAt, attempts, lastError }`. Operations on the same item are coalesced (e.g. create+update → create; create+delete → drop). Entries replay in FIFO order across items; linked groups replay in their own order.
  - Coalescing applies only to the item's latest entry, and only when it is not in a linked group and not in flight. Rules: create+update → create (new payload); create+delete → no entry, item removed; update+update → update (first base etag kept); update+delete → delete.
  - The sync engine marks an entry in flight before sending it; a later local change then gets its own entry. In-flight marks are cleared when the store opens (crash recovery).
  - A coalesced entry keeps its original queue position.
- R5. Every UI write goes to the local store + outbox in one transaction. The UI never waits for the network.
- R6. Connectivity detection (NetworkInformation) triggers an outbox flush when the network returns. An account counts as online only once its server answers, so a captive portal shows as Offline ([ui-states](ui-states.md) R2).
  - Interim (until the sync engines exist): Windows reporting the network back requests a sync; "online once the server answers" comes with [sync-ms365](sync-ms365.md) and [sync-radicale](sync-radicale.md).
- R7. Failed outbox entries back off exponentially, capped at 15 minutes; Sync now resets the backoff. Permanent failures (4xx other than 401/412 and the consent-revoked 403 of [sync-ms365](sync-ms365.md) R11; 409 counts as permanent) are shown to the user with options to retry or discard; a discarded entry is moved to Conflicts ([conflict-resolution](conflict-resolution.md) R9). Entries for an account in "auth required" are held until sign-in succeeds.
  - First retry after 30 s, doubling each time, capped at 15 minutes. A permanent failure is not retried until the user retries it.
  - Discard (built with [ui-states](ui-states.md) slice 1, after a confirmation): every queued entry for the item is dropped and its local version goes to Conflicts. A create that never reached the server leaves the calendar. For an item the server has, it stays on the calendar as it is locally until the next sync, which fetches the server's version again: the item's etag is cleared and its collection is marked for a full re-read, so the engine drops its sync token or delta link for that collection once. An entry in a linked group cancels the group's later entries, as for a conflict.
  - Interim: the auth-required hold comes with [account-management](account-management.md).
- R8. Sync state per collection: Graph deltaLink(s) per window chunk; CalDAV sync-token and CTag.
- R9. Items pending upload are visibly marked (e.g. a small sync glyph).
- R9a. Pending changes list: clicking the "n changes waiting" status ([ui-states](ui-states.md) R3) opens a list of outbox entries showing the item, the operation, and its state (waiting, or failed with the reason), with Retry and Discard on failed entries.
  - R9a is built with [ui-states](ui-states.md) slice 1: a popup from the toolbar status, like the conflicts list, one row per entry (title, Create / Edit / Delete, "Waiting" or "Failed: reason"); Retry and Discard on failed rows. R9 (the glyph on cards) comes later with [calendar-views](calendar-views.md).
- R10. Sync scheduling: every 5 minutes; immediately after a local change; on app focus; on network reconnect; plus a manual "Sync now" button (UI toolbar and tray).
  - One sync runs at a time; a request during a run causes exactly one more run afterwards.
  - Within a run, all accounts sync in parallel. Within an account, queued changes are sent first, in outbox order; then its collections (and subscribed feeds) are pulled in parallel, at most 4 at a time per account (Graph allows 4 concurrent requests per mailbox). A failure that stops an account (sign-in, throttling, unreachable) stops only that account.
  - Each collection reports when its pull starts and ends, for the sidebar indicator ([ui-states](ui-states.md) R1).
  - Every calendar, task list and feed is pulled, whether ticked in the sidebar or not ([calendar-sets](calendar-sets.md) R7). Changed 2026-11-11; before that, unticked ones were not pulled and ticking one synced only it.
  - Interim (until the sync engines exist): a sync only writes to the log.

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
