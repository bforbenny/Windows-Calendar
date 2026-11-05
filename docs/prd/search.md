---
description: Offline full-text search across cached events and tasks from all accounts
when-to-use: Before working on search UI or the full-text index
tags: [search, fts, ui]
status: pending
---

# Search

## Summary

A search box finds events and tasks across all accounts using a local SQLite FTS5 index. It works offline and returns results as you type.

## Requirements

- R1. FTS5 virtual table indexing title, description/notes, location and calendar/list name. It is updated in the same transaction as item writes.
- R2. Search box in the toolbar (Ctrl+F / Ctrl+E). Results update as you type (debounced ~150 ms), with prefix matching.
- R3. Results list: type icon (event/task), title, date/time, calendar colour + name, and a snippet with the match highlighted. Upcoming items first (soonest first), then past items (newest first).
- R4. Filters: All / Events / Tasks; account or calendar. The active calendar set is the default filter, shown as a removable chip; "Clear filters" removes it.
- R5. Selecting a result navigates the calendar to the date shown and opens that occurrence.
- R6. Scope: everything in the local store (the sync window for MS365; full collections for Radicale). Online server-side search beyond the window is out of scope. When MS365 accounts exist, results show "Microsoft 365 searched from <date>" so the window is visible. Declined events are included ([attendees-invitations](attendees-invitations.md) R4).
- R7. Recurring series appear once (series result), with the next occurrence date. If the series has ended, the last occurrence is shown; if the match is only in an exception, that occurrence is shown.
- R8. Search across ~10,000 items returns in < 100 ms.

## Out of scope

- Server-side search (Graph `$search`, CalDAV text-match).
- Saved searches, advanced query syntax.
- Searching attendee names.

## Acceptance criteria

- [ ] Searching a word from an event's location, description or title finds it, offline.
- [ ] Results include both MS365 and Radicale items.
- [ ] An item edited in the app is searchable by its new text immediately.

## Dependencies

- [local-store-offline](local-store-offline.md), [event-location](event-location.md)
