---
description: On-device NPU model and rule-based date chips turn natural-language search queries into filters, with attendee names indexed
when-to-use: Before working on natural-language search, search filters (search R4), or attendee indexing
tags: [search, ai, npu, filters, fts]
status: pending
---

# Natural-language search (NPU)

## Summary

Typing in the search box keeps giving instant FTS results ([search](search.md)). Common date phrases ("next week", "last month") become filter chips at once through the quick-add date rules. After a short pause, the on-device model ([on-device-model](on-device-model.md)) turns the rest of a phrase like "meetings with Alex last month about budget" into removable chips (dates, type, calendar, attendee, location) plus leftover keywords, and the results refresh once. This work also builds the search filters ([search](search.md) R4) and adds attendees to the index.

## Requirements

- R1. Rule chips: date-range phrases the quick-add date rules recognise (`today`, `tomorrow`, `this/next/last week`, `this/next/last month`, weekday names, explicit dates) become a date chip instantly, without the model. If the date rules are not built yet, this falls to the model.
- R2. Model trigger: after ~600 ms without typing, for queries of 2 or more words that still contain words not consumed by rule chips. A newer request cancels the previous one. Results are cached per (query, date) for the session.
- R3. Indicator: same as [nl-quick-add](nl-quick-add.md) R4, on the search box.
- R4. Model output is a flat JSON object: keywords, date range (none / relative past or future range / named range), type (all/events/tasks), calendar or account hint, attendee names, location. Schema limits per [on-device-model](on-device-model.md) R1; prompt per R8 with calendar and account names.
- R5. Applying model output:
  - code resolves and validates the date range and calendar hint; ungrounded or invalid fields are dropped ([on-device-model](on-device-model.md) R6, R7);
  - output with no filter (keywords only) is ignored and plain FTS results stay;
  - each filter is a removable chip; the remaining keywords drive the FTS match; the typed text is never changed.
- R6. Removed chips stay removed: a chip the user removes is not re-added by the rules or the model for the same query text. Editing the text clears this memory.
- R7. Empty fallback: if the filtered results are empty, plain FTS results for the typed text are shown under "No matches with these filters".
- R8. Sync window: when a date chip starts before the earliest MS365 sync date, the chip shows "searched from <date>" ([search](search.md) R6).
- R9. Filters ([search](search.md) R4): All / Events / Tasks, account or calendar, date range, attendee, location. They can also be set by hand. The calendar-set default chip stays with [calendar-sets](calendar-sets.md).
- R10. Attendee indexing:
  - a new FTS column holds attendee and organiser display names and email addresses, from Graph `attendees` and `organizer`, and CalDAV `ATTENDEE` and `ORGANIZER` (CN and address);
  - FTS5 cannot add a column in place, so the migration rebuilds the index from existing items once;
  - the column is updated in the same transaction as item writes;
  - plain search terms also match this column, so a name finds that person's meetings; an attendee chip matches this column only.
- R11. FTS-only results still return in < 100 ms for ~10,000 items ([search](search.md) R8); the model never blocks typing.
- R12. Eval: a query phrase set with expected chips and keywords, run with the [on-device-model](on-device-model.md) R11 harness.

## Out of scope

- Semantic or embedding search, AppContentIndexer.
- Server-side search beyond the sync window.
- Acting on results by phrase ("delete all ...").

## Acceptance criteria

- [ ] "next week" shows a date chip instantly, with no model call.
- [ ] "meetings with Alex last month about budget" shows chips for Events, last month and attendee Alex, with "budget" as the keyword, and finds the matching event.
- [ ] Searching an attendee's or organiser's name finds their meetings, offline.
- [ ] Single-word queries never call the model.
- [ ] A removed chip does not come back after the next pause.
- [ ] Filters that match nothing show the plain FTS results under "No matches with these filters".
- [ ] A date chip before the MS365 sync window shows "searched from <date>".
- [ ] With AI off or unavailable, search behaves as [search](search.md) plus rule chips, and filters can still be set by hand.

## Dependencies

- [on-device-model](on-device-model.md), [search](search.md), [quick-add](quick-add.md) (date rules, optional), [attendees-invitations](attendees-invitations.md), [local-store-offline](local-store-offline.md)
