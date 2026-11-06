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
- R7 holds: unshiftable rules can still get this-occurrence edits and series text edits.
- A conflict covers the whole resource (the series), per [conflict-resolution](conflict-resolution.md) R8.

## Interim (rule editing R3, CalDAV)

- The editor has a **Repeat** dropdown (Doesn't repeat / Daily / Weekly / Monthly / Yearly). Choosing one shows these inline: **Every N** day(s)/week(s)/month(s)/year(s); weekday toggles for Weekly (preset to the start's weekday, at least one always selected); for Monthly, **On day D** or **On the 1st/2nd/3rd/4th/last weekday** (from the start date); Yearly repeats on the start date; **Ends** Never / After N times / On date.
- The rule is written as one RRULE (FREQ, INTERVAL, BYDAY, BYMONTHDAY, COUNT or UNTIL). UNTIL takes the same form as for a split (UTC for TZID and UTC series, floating for floating, DATE for all-day), inclusive of the chosen date.
- A rule the controls cannot represent (R7) is shown read-only as before, with the controls disabled and a notice.
- New events and single events: rule changes autosave like the other fields, with no scope question.
- Existing series: rule changes are staged, and an **Apply** button appears in the Repeat section. Apply asks for the scope (**This and following events** or **All events in the series**; there is no This event option) and then saves once. Closing the editor with unapplied rule changes asks whether to discard them.
  - All events: the master's RRULE is replaced. Overrides and EXDATEs that are not occurrences of the new rule are dropped, after a confirmation ("N changed occurrences will be lost").
  - This and following: the series is split as in R6, and the new series gets the new rule. The same confirmation applies to the overrides that move to the new series.
  - On the first occurrence, This and following acts as All (R6). Not offered for meetings you organise (R6).
- **Doesn't repeat** on an existing series: with All, the series becomes a single event at its first occurrence (overrides and EXDATEs are dropped, after confirmation). With This and following, the series ends before this occurrence and this occurrence becomes a new single event, one linked outbox group.
- Not yet: undo for rule changes, and rule creation from quick add.

## Interim (MS365: this event, entire series, this and following, rule editing)

- The same scope question, editor and Repeat panel as for CalDAV, for MS365 events that are not meetings ([sync-ms365](sync-ms365.md) second slice). Each Graph occurrence is its own stored event; its series master is stored too (R2).
- This event (R4): the occurrence is PATCHed (Graph makes it an exception) or DELETEd.
- Entire series (R5): title, location and description are PATCHed on the master. A time change moves the master's start and end by the same amount; a move to another day shifts the pattern like CalDAV (weekly days of week, monthly day of month, nth weekday to the new weekday, yearly day and month), and `recurrence.range.startDate` follows the start. Switching a series between all-day and timed is refused, as for CalDAV.
- This and following (R6): the master's `recurrence.range` ends the day before the occurrence (a numbered range becomes an end date), and a new series is POSTed starting at the occurrence with the rest of the pattern (a numbered range keeps the occurrences left). Both are one linked outbox group, truncate first. On the first occurrence it acts as All.
- Rule editing (R3): the Repeat panel reads and writes Graph patterns: daily, weekly (days of week), absoluteMonthly (day D), relativeMonthly (1st-4th or last weekday), absoluteYearly; ranges noEnd, numbered (After N times) and endDate (On date, inclusive). Other patterns (e.g. relativeYearly, several weekdays on a monthly pattern) are shown read-only (R7). Doesn't repeat with All replaces the series with a single event at its first occurrence (a create, then the series delete, one linked group); with This and following the series ends before this occurrence and this occurrence becomes a single event.
- Exchange drops a series' exceptions when its time or pattern changes, and drops the original's exceptions after a split point. Before such a change the app counts the stored exceptions affected and asks ("N changed occurrences will be lost"); cancelling saves nothing.
- Series-wide time and rule changes reach the occurrences when the sync that sends them pulls the calendar again (seconds when online; on reconnect when offline). Title and location changes show at once. A new series (or the new half of a split) shows its first occurrence until it is sent.
- Undo works for drags (one occurrence, the series, or a split) and for deleting this and following events, on the master for series changes. Deleting one occurrence has no Undo yet.
- A series cannot be moved to another calendar from the editor (its exceptions would be lost); single events can.

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
