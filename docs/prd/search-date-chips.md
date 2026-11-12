---
description: Date phrases typed in search (next week, monday, march) become removable date filter chips, using the quick-add parser
when-to-use: Before working on search date chips, the phrase-level output of the quick-add parser, or how search reads date phrases
tags: [search, quick-add, parsing, dates]
status: pending
---

# Search date chips

## Summary

Common date phrases typed in the search box ("next week", "monday", "march") become date filter chips at once, using the quick-add date rules. The parser reports the phrase; quick add and search each decide what it means. Chips are filters as defined in [search-filters](search-filters.md).

## Requirements

- R1. `QuickAddParser` gains phrase-level output: it reports the recognised date phrase and its span, with a kind (today/tomorrow, weekday, this/next/last week, this/next/last month, bare month name, explicit date), without resolving meaning. Quick add and search each turn the phrase into dates. Existing quick-add results are unchanged, guarded by a regression test that runs every existing quick-add test phrase and compares results.
- R2. Search meanings, as whole calendar periods (week start from the app's week-start setting):
  - `today`, `tomorrow`: that day;
  - `this/next/last week`: the full calendar week, past days included;
  - a weekday: that day in the current week;
  - `this/next/last month`: the full calendar month;
  - a bare month name (e.g. `march`): that whole month in the current year;
  - an explicit date: that day.
- R3. Chips appear instantly as you type and never block typing. The words a chip consumes are removed from the FTS words; the typed text is never changed.
- R4. A chip the user removes is not re-added for the same query text; editing the text clears this memory.
- R5. Consumed-word title match: the words a chip consumes are also searched in titles. Title matches outside the chip's range show below the filtered results (e.g. "monday standup" still finds "Monday standup" in another week).
- R6. Chips are filters as defined in [search-filters](search-filters.md): same chip row, removal, empty fallback and sync-window note.

## Out of scope

- Language-model parsing (parked with [on-device-model](on-device-model.md)).
- Semantic search.

## Acceptance criteria

- [ ] "next week" shows a date chip for the full calendar week after this one, instantly, and typing is not delayed.
- [ ] "budget next week" shows the chip with "budget" as the keyword and finds the matching event in next week; the text in the box is unchanged.
- [ ] With the week-start setting on Monday and then Sunday, "this week" covers Monday-Sunday and Sunday-Saturday respectively, including days already past.
- [ ] "tomorrow" is that day; "monday" is Monday of the current week; "last month" is the full previous month; "march" is all of March in the current year; "5 dec" is that day.
- [ ] A removed chip does not come back while the text is unchanged; editing the text lets it appear again.
- [ ] "monday standup" shows the Monday chip and its matches, with "Monday standup" in another week listed below the filtered results.
- [ ] The regression test runs every existing quick-add test phrase and the results match those before the parser change.
- [ ] A chip before the MS365 sync window shows "searched from <date>"; with no matches, plain FTS results show under "No matches with these filters".

## Dependencies

- [search-filters](search-filters.md), [quick-add](quick-add.md)
