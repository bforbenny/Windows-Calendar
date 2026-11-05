---
description: Two-way CalDAV sync of events (VEVENT) and tasks (VTODO) with self-hosted Radicale servers
when-to-use: Before working on anything that talks CalDAV/WebDAV to Radicale
tags: [sync, caldav, radicale, webdav, vtodo]
status: pending
---

# Radicale sync (CalDAV)

## Summary

Each Radicale account syncs all its calendar collections (VEVENT and/or VTODO) two-way over HTTPS with basic auth. RFC 6578 sync-collection is used for incremental sync, with a full-sync fallback. A small CalDAV client is written in-house on top of HttpClient + Ical.Net, because the existing .NET CalDAV libraries are small and lightly maintained.

## Requirements

- R1. Discovery: `PROPFIND` current-user-principal → calendar-home-set → `PROPFIND Depth:1` collections, reading displayname, calendar-color, supported-calendar-component-set, getctag, sync-token. A collection supporting both VEVENT and VTODO maps to two logical collections ([account-management](account-management.md) R1).
- R2. Incremental sync: `REPORT sync-collection` (Depth: 0) with the stored sync-token; then `REPORT calendar-multiget` for the changed hrefs; handle 404 entries as deletions.
- R3. **Token fallback:** if the server rejects the token (`DAV:valid-sync-token` precondition, typically 403/409), do a full sync: list all hrefs + etags, diff against local, multiget the changes.
- R4. Quick check: compare getctag/sync-token first and skip the collection if unchanged.
- R5. XML parsing is namespace-aware. Radicale emits DAV elements in the default namespace without a prefix.
- R6. Writes: `PUT` with `If-Match: <etag>` for updates; `If-None-Match: *` for creates; `DELETE` with `If-Match`. Status 412 → [conflict-resolution](conflict-resolution.md).
- R7. New items get a UUID-based UID and resource name `<uid>.ics`.
- R8. Round-trip safety: edit the stored iCalendar object (Ical.Net) and re-serialise it, preserving unknown properties (X-*, GEO, VALARM, etc.). Never rebuild it from normalised columns.
- R9. Sync the whole collection (no time-range filter), since Radicale's time-range query support is limited. Recurrence expansion happens locally.
- R10. HTTPS only, TLS certificate validation on. Basic auth header built per request from the vault credential; never logged. An untrusted certificate gives the message in [account-management](account-management.md) R4a.
- R11. 401 → mark the account "auth required"; do not loop. Prompting follows [sync-ms365](sync-ms365.md) R11: a toast and the Sign in bar while hidden, and the masked password prompt only when the user clicks Sign in.

## Out of scope

- CardDAV (contacts).
- Scheduling (iTIP/iMIP, CalDAV scheduling outbox).
- Creating/deleting collections on the server.
- Non-Radicale CalDAV servers are not targeted in v1, though standard behaviour should make them likely to work.

## Acceptance criteria

- [ ] Events and tasks created in another CalDAV client (e.g. Thunderbird/DAVx5) appear in the app within one sync cycle, and vice versa.
- [ ] Deleting the sync-token cache on the server (forced token invalidation) triggers a successful full resync with no duplicates.
- [ ] Unknown properties (e.g. X-APPLE-*, GEO) survive an edit made in the app.
- [ ] Concurrent edit on the server and in the app yields a 412 handled per conflict policy.

## Dependencies

- [account-management](account-management.md), [local-store-offline](local-store-offline.md), [conflict-resolution](conflict-resolution.md), [recurring-events](recurring-events.md), [timezone-handling](timezone-handling.md)
