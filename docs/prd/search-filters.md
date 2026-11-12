---
description: Filter chips and a Filters flyout in the search popup (type, calendar, date range, attendee, location), applied in SQL
when-to-use: Before working on search filters, filter chips, filter-only search or the empty-results fallback
tags: [search, filters, ui]
status: closed
---

# Search filters

## Summary

Delivers [search](search.md) R4. The search popup gets a chip row and a Filters flyout so results can be narrowed by type, calendar, date range, attendee and location, with or without typed words. Filters are applied in SQL before the result limit.

## Requirements

- R1. A chip row sits above the results in the search popup, plus a "Filters" button that opens a flyout to add a filter. Active filters show as removable chips. Typed date chips ([search-date-chips](search-date-chips.md)) look and behave the same.
- R2. Filters:
  - All / Events / Tasks;
  - account or calendar (visible calendars and task lists only);
  - date range;
  - attendee: suggestions come from the indexed people ([search-attendees](search-attendees.md)); picking one filters by that person and matches the `people` column only;
  - location: free text, matches the `location` column.
- R3. Filters are applied in SQL before the result limit, not after the C# sort. With a date filter a recurring series shows its occurrence inside the range; ranges reach only as far as stored occurrences.
- R4. Filter-only search: with filters or chips and no words, every matching item is listed (no FTS MATCH), in the same sort order, limit 50 with "Show more".
- R5. Under a date filter, undated tasks are excluded; completed tasks are included or excluded exactly as in search without filters (unchanged).
- R6. Empty fallback: if filtered results are empty and words were typed, plain FTS results for the words show under "No matches with these filters". "Clear filters" removes all chips ([ui-states](ui-states.md)).
- R7. Sync window: when a date filter starts before the earliest MS365 sync date, its chip shows "searched from <date>" ([search](search.md) R6).
- R8. Hand-set filters persist for the session (until cleared or app restart); closing the popup does not clear them.
- R9. Keyboard: Backspace in an empty search box removes the last chip; chips are keyboard-focusable and removable with Delete or Enter.
- R10. The calendar-set default chip stays with [calendar-sets](calendar-sets.md).
- R11. Results return in < 100 ms for ~10,000 items with filters active, covered by the performance test in [search-attendees](search-attendees.md) R6.

## Out of scope

- Saved searches.
- Server-side search beyond the sync window.
- Acting on results.

## Acceptance criteria

- [x] The Filters button opens a flyout; adding a filter shows a removable chip above the results.
- [x] Events and Tasks filters list only that type; the calendar filter lists only visible calendars and task lists.
- [x] A date-range filter with a word lists a recurring series' occurrence inside the range, and more than 50 matching items are cut at 50 only after filtering.
- [x] An attendee filter chosen from the suggestions lists only that person's meetings; text in a title or description naming them does not match.
- [x] A location filter lists only items whose location matches.
- [x] With chips and no words, matching items are listed with "Show more" after 50.
- [x] Under a date filter, undated tasks are not listed; completed tasks with a date in range appear as they do in unfiltered search.
- [x] A filter that matches nothing, with words typed, shows plain FTS results under "No matches with these filters"; "Clear filters" removes every chip.
- [x] A date filter starting before the MS365 sync window shows "searched from <date>" on its chip.
- [x] Filters set by hand are still active after closing and reopening the popup, and are gone after restarting the app.
- [x] Backspace in an empty box removes the last chip; Tab reaches a chip and Delete or Enter removes it.
- [x] The performance test passes with filters active on ~10,000 items.

## Dependencies

- [search](search.md), [search-attendees](search-attendees.md), [calendar-sets](calendar-sets.md), [ui-states](ui-states.md)
