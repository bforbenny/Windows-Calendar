---
description: Two-way CalDAV sync of events (VEVENT) and tasks (VTODO) with self-hosted Radicale servers
when-to-use: Before working on anything that talks CalDAV/WebDAV to Radicale
tags: [sync, caldav, radicale, webdav, vtodo]
status: closed
---

# Radicale sync (CalDAV)

## Summary

Each Radicale account syncs all its calendar collections (VEVENT and/or VTODO) two-way over HTTPS with basic auth. RFC 6578 sync-collection is used for incremental sync, with a full-sync fallback. A small CalDAV client is written in-house on top of HttpClient + Ical.Net, because the existing .NET CalDAV libraries are small and lightly maintained.

## Requirements

- R1. Discovery: `PROPFIND` current-user-principal → calendar-home-set → `PROPFIND Depth:1` collections, reading displayname, calendar-color, supported-calendar-component-set, getctag, sync-token. A collection supporting both VEVENT and VTODO maps to two logical collections ([account-management](account-management.md) R1).
  - Each distinct collection URL is synced once; each resource goes to the Calendar or TaskList logical collection by its component (VEVENT / VTODO). Sync state is stored on the first collection for that URL.
- R1a. Subscribed calendars: a collection with resourcetype `CS:subscribed` (`http://calendarserver.org/ns/`) holds only a `CS:source` feed URL; Radicale does not fetch the feed. It becomes a read-only calendar, and the app downloads the feed itself:
  - `webcal://` is fetched as `https://`; plain `http://` feeds are not fetched and are marked "not secure" in the sidebar (R10). Redirects are followed only to HTTPS.
  - No credentials are sent to the feed host, and the feed URL is never logged (it often carries a private token).
  - At most once an hour, with `If-None-Match` / `If-Modified-Since`; Sync now fetches regardless.
  - Each UID in the feed (master, overrides and the timezones it uses) becomes one cached item; items that left the feed are removed. A feed that fails is logged and does not stop the account's sync.
  - Subscribed collections are skipped by sync-collection and never written to.
- R2. Incremental sync: `REPORT sync-collection` (Depth: 0) with the stored sync-token; then `REPORT calendar-multiget` for the changed hrefs; handle 404 entries as deletions.
  - Multiget in batches of 100. Items are keyed by href (`remote_id`); the UID stays in the payload.
- R3. **Token fallback:** if the server rejects the token (`DAV:valid-sync-token` precondition, typically 403/409), do a full sync: list all hrefs + etags, diff against local, multiget the changes.
  - Implemented as sync-collection with an empty token: the server returns every href and etag. Local items for that URL missing from the result are deleted, except items with pending outbox entries.
- R4. Quick check: compare getctag/sync-token first and skip the collection if unchanged.
- R5. XML parsing is namespace-aware. Radicale emits DAV elements in the default namespace without a prefix.
- R6. Writes: `PUT` with `If-Match: <etag>` for updates; `If-None-Match: *` for creates; `DELETE` with `If-Match`. Status 412 → [conflict-resolution](conflict-resolution.md).
- R7. New items get a UUID-based UID and resource name `<uid>.ics`.
- R8. Round-trip safety: edit the stored iCalendar object (Ical.Net) and re-serialise it, preserving unknown properties (X-*, GEO, VALARM, etc.). Never rebuild it from normalised columns.
- R9. Sync the whole collection (no time-range filter), since Radicale's time-range query support is limited. Recurrence expansion happens locally.
- R10. HTTPS only, TLS certificate validation on. Basic auth header built per request from the vault credential; never logged. An untrusted certificate gives the message in [account-management](account-management.md) R4a.
- R11. 401 → mark the account "auth required"; do not loop. Prompting follows [sync-ms365](sync-ms365.md) R11: a toast and the Sign in bar while hidden, and the masked password prompt only when the user clicks Sign in.
  - Interim (until [ui-states](ui-states.md) R2): a 401 is logged once and the account is paused until the app restarts.

## Interim (second slice: push)

- Each account sync first sends its due outbox entries (R6), then pulls. A new item's href (`<collection>/<uid>.ics`, R7) is set when it is created locally, so a send that succeeded before a crash is never duplicated.
- After a successful write the item takes the etag from the response; later queued entries for the same item are rebased onto it, so they do not raise a false 412. If the server sends no etag, the next pull fetches the item again.
- Responses: 412 → [conflict-resolution](conflict-resolution.md); 404 on DELETE → done; 401 → R11; network errors and 5xx → backoff ([local-store-offline](local-store-offline.md) R7); other 4xx (incl. 409) → permanent failure. A network error stops sending for that account until the next sync.
- Task (VTODO) writes were added with [tasks](tasks.md) slice 3 (package 1.0.35.0), through the same PUT / DELETE path.
- Each sync starts with re-discovery ([account-management](account-management.md) R11), so new calendars and subscriptions appear without re-adding the account; Sync now is the manual refresh.

## Out of scope

- CardDAV (contacts).
- Scheduling (iTIP/iMIP, CalDAV scheduling outbox).
- Creating/deleting collections on the server.
- Non-Radicale CalDAV servers are not targeted in v1, though standard behaviour should make them likely to work.

## Acceptance criteria

- [x] Events and tasks created in another CalDAV client (e.g. Thunderbird/DAVx5) appear in the app within one sync cycle, and vice versa.
- [x] Deleting the sync-token cache on the server (forced token invalidation) triggers a successful full resync with no duplicates.
- [x] Unknown properties (e.g. X-APPLE-*, GEO) survive an edit made in the app.
- [x] Concurrent edit on the server and in the app yields a 412 handled per conflict policy.

## Dependencies

- [account-management](account-management.md), [local-store-offline](local-store-offline.md), [conflict-resolution](conflict-resolution.md), [recurring-events](recurring-events.md), [timezone-handling](timezone-handling.md)
