---
description: Recurring event model across Graph and CalDAV, and edit scopes this-occurrence / this-and-following / whole-series, online and offline
when-to-use: Before working on recurrence rules, occurrence expansion, or editing/deleting recurring events or tasks
tags: [recurrence, rrule, events, sync, offline]
status: in-progress
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

## Interim (CalDAV: this occurrence, this and following, entire series)

- R4, R5 and R6 for drag ([calendar-views](calendar-views.md) R7), editor changes and delete, on Radicale; MS365 comes later. The scope question is a radio list (**This event** selected, **This and following events**, **All events in the series**) with OK and Cancel.
- The occurrence index stores each occurrence's original start (its RECURRENCE-ID), so a card, the editor and a delete know which occurrence they act on.
- This occurrence: an override VEVENT is added or replaced; its RECURRENCE-ID uses the same form as the master's DTSTART (TZID, floating, UTC or DATE). Deleting it adds an EXDATE and drops the override.
- Entire series: title, location and description are written to the master (overrides keep their own values). A time change moves the master by the same amount and sets its length; EXDATEs and override RECURRENCE-IDs move with it, and overrides whose times were not changed move too. A move to another day shifts the rule: weekly BYDAY by the same number of days, monthly and yearly BYMONTHDAY by days, and ordinal BYDAY ("2nd Tuesday") to the new weekday. A rule that cannot be shifted that way (BYSETPOS, BYYEARDAY, BYWEEKNO, several BYMONTHDAY values, or a day that would pass 28) refuses the move with a message. So do switching a whole series between all-day and timed, and moving a series that has RDATEs; This event still works for both.
- This and following (R6): the original ends before the occurrence (UNTIL as UTC for TZID and UTC series, floating for floating ones, a DATE for all-day ones; a COUNT becomes an UNTIL). Its overrides and EXDATEs from the occurrence on move to the new series, which gets a new UID, starts at the occurrence and keeps the rest of the rule (a COUNT is reduced by the occurrences before the split, counting excluded ones, RFC 5545). The change is then applied to the new series as an entire-series change. Delete with this scope only ends the original. The truncate and the create are one linked outbox group. After a split the editor follows the new series.
- The organiser check for R6: not offered when the series has attendees and its ORGANIZER is the account's email address.
- The editor shows the rule read-only ("Repeats weekly on Monday"); creating or changing rules (R3) comes later. R7 holds: unshiftable rules can still get this-occurrence edits and series text edits.
- A conflict covers the whole resource (the series), per [conflict-resolution](conflict-resolution.md) R8.

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
