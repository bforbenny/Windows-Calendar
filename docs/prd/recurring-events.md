---
description: Recurring event model across Graph and CalDAV, and edit scopes this-occurrence / this-and-following / whole-series, online and offline
when-to-use: Before working on recurrence rules, occurrence expansion, or editing/deleting recurring events or tasks
tags: [recurrence, rrule, events, sync, offline]
status: pending
---

# Recurring events

## Summary

Recurring events can be created, edited and deleted with three scopes: **this occurrence**, **this and following**, and **entire series**. This works offline, with the changes queued in the outbox. The two backends model recurrence differently, so the local model unifies them.

## Background

- **CalDAV:** one resource holds a master VEVENT (RRULE/RDATE/EXDATE) and override VEVENTs keyed by RECURRENCE-ID.
- **Graph:** a `seriesMaster` event with a `recurrence` pattern, plus `occurrence` and `exception` instances, each with its own id. There is no native "split series" operation.

## Requirements

- R1. Unified local model: series master + exceptions (overrides) + cancelled occurrences, and the expanded occurrence index (see [local-store-offline](local-store-offline.md)).
- R2. Expansion: CalDAV series are expanded locally with Ical.Net. Graph occurrences come from calendarView, but the series master is stored too.
- R3. Create a recurring event with: daily / weekly (by weekday) / monthly (by date or nth weekday) / yearly, an interval, and an end that is never, after N occurrences, or on a date. Only patterns representable in **both** Graph and RRULE are offered.
- R4. **This occurrence:** CalDAV → add/modify an override with RECURRENCE-ID (or EXDATE for a delete). Graph → PATCH/DELETE the occurrence instance. Moving an occurrence onto or past its neighbour is refused before saving ([calendar-views](calendar-views.md) R7).
- R5. **Entire series:** CalDAV → modify the master. Graph → PATCH the seriesMaster.
- R6. **This and following (split):**
  - CalDAV: set UNTIL on the original master before the split point (dropping overrides after it), and create a new resource with a new UID starting at the split point. Copy the overrides after the split into the new series where they still apply.
  - Graph: PATCH the original seriesMaster's `recurrence.range` to end before the split, and POST a new series starting at the split point.
  - Offline: queue both operations as one linked outbox group, applied in order (truncate first, then create). If the truncate fails with a conflict, do not create the new series; route it to [conflict-resolution](conflict-resolution.md).
  - On the first occurrence, "this and following" acts as "all".
  - Not offered for meetings with attendees that you organise ([attendees-invitations](attendees-invitations.md) R8).
  - Undo restores the dropped overrides ([undo](undo.md) R5).
- R7. Recurrence rules received from servers that the editor cannot represent (e.g. complex BYSETPOS, RDATE lists) are displayed and preserved. Editing such a series is limited to this-occurrence plus non-recurrence fields, with a notice.
- R8. Recurring tasks: display and complete only (completing advances to the next instance per server behaviour). Completed offline, the task shows as completed until the next sync brings the next instance. Rule editing for tasks is out of scope for v1. See [tasks](tasks.md).

## Out of scope

- Editing complex recurrence rules beyond R3.
- Recurrence rule editing for tasks.

## Acceptance criteria

- [ ] For each backend: create a weekly series; edit one occurrence; delete one occurrence; edit the whole series; split with "this and following" — each result is verified in Outlook / another CalDAV client.
- [ ] The same five operations performed offline sync correctly on reconnect.
- [ ] A series with an unsupported rule from the server survives an edit to its title unchanged.

## Dependencies

- [local-store-offline](local-store-offline.md), [sync-ms365](sync-ms365.md), [sync-radicale](sync-radicale.md), [timezone-handling](timezone-handling.md), [conflict-resolution](conflict-resolution.md)

## Open questions

- Graph split: Graph may regenerate exception ids on the original series when its range changes. Verify this during implementation.
