---
description: Rule-based date chips and search filters turn common search phrases into filters, with attendee names indexed
when-to-use: Before working on search date chips, search filters (search R4), or attendee indexing
tags: [search, filters, fts]
status: parked
---

# Search filters and date chips

> Parked on 2026-11-12: split into [search-attendees](search-attendees.md), [search-filters](search-filters.md) and [search-date-chips](search-date-chips.md). Not revisited; read those instead.
>
> Rescoped on 2026-11-11: the on-device model part was dropped after the NPU spike was NO-GO ([on-device-model](on-device-model.md), parked). The rule-based chips, filters and attendee indexing remain.

## Summary

Typing in the search box keeps giving instant FTS results ([search](search.md)). Common date phrases ("next week", "last month") become removable filter chips at once through the quick-add date rules. This work also builds the search filters ([search](search.md) R4) and adds attendees to the index, so a person's name finds their meetings.

## Requirements

- R1. Rule chips: date-range phrases the quick-add date rules recognise (`today`, `tomorrow`, `this/next/last week`, `this/next/last month`, weekday names, explicit dates) become a date chip instantly. The words a chip consumes are removed from the FTS match; the typed text is never changed. If the date rules are not built yet, no chips appear and search stays plain FTS.
- R2. Each chip is removable. A chip the user removes is not re-added for the same query text; editing the text clears this memory.
- R3. Empty fallback: if the filtered results are empty, plain FTS results for the typed text are shown under "No matches with these filters".
- R4. Sync window: when a date chip starts before the earliest MS365 sync date, the chip shows "searched from <date>" ([search](search.md) R6).
- R5. Filters ([search](search.md) R4): All / Events / Tasks, account or calendar, date range, attendee, location, set by hand. The calendar-set default chip stays with [calendar-sets](calendar-sets.md).
- R6. Attendee indexing:
  - a new FTS column holds attendee and organiser display names and email addresses, from Graph `attendees` and `organizer`, and CalDAV `ATTENDEE` and `ORGANIZER` (CN and address);
  - FTS5 cannot add a column in place, so the migration rebuilds the index from existing items once;
  - the column is updated in the same transaction as item writes;
  - plain search terms also match this column, so a name finds that person's meetings; an attendee filter matches this column only.
- R7. Results still return in < 100 ms for ~10,000 items ([search](search.md) R8); chips never block typing.

## Out of scope

- Natural-language parsing by a language model (parked with [on-device-model](on-device-model.md)).
- Semantic or embedding search, AppContentIndexer.
- Server-side search beyond the sync window.
- Acting on results by phrase ("delete all ...").

## Acceptance criteria

- [ ] "next week" shows a date chip instantly.
- [ ] "budget next week" shows a next-week date chip with "budget" as the keyword, and finds the matching event.
- [ ] Searching an attendee's or organiser's name finds their meetings, offline.
- [ ] A removed chip does not come back while the text is unchanged.
- [ ] Filters that match nothing show the plain FTS results under "No matches with these filters".
- [ ] A date chip before the MS365 sync window shows "searched from <date>".
- [ ] Filters can be set by hand without typing a date phrase.

## Dependencies

- [search](search.md), [quick-add](quick-add.md) (date rules, optional), [attendees-invitations](attendees-invitations.md), [local-store-offline](local-store-offline.md)
