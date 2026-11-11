---
description: Typed quick add that parses English phrases like "Lunch with Sam fri 1pm at Cafe Rio" into an event or task, offline and rule-based
when-to-use: Before working on the quick-add box, its parser, or quick-create flows
tags: [ui, quick-add, parsing, events, tasks]
status: pending
---

# Quick add

## Summary

A single text box turns a typed phrase into an event (or a task) with date, time, duration, location and calendar filled in. Parsing is deterministic and rule-based, written in-house and run locally. No AI service, token, network call or third-party NLP package is involved, so it works offline.

## Requirements

- R1. Available in: the quick-create popup ([calendar-views](calendar-views.md) R5), the toolbar (Ctrl+N focuses it), and the [tray-flyout](tray-flyout.md).
- R2. Recognised tokens (English, case-insensitive):
  - Dates: `today`, `tomorrow`, weekday names (next occurrence), `next <weekday>`, `in <n> days/weeks`, `5 Dec` / `Dec 5`, numeric dates in the Windows short-date order (e.g. `5/12` = 5 December for en-AU).
  - Times: `1pm`, `1:30pm`, `13:00`, `noon`, `midnight`; ranges `1-2pm`, `1pm to 3pm`, `9:00-10:30`.
  - Duration: `for 30m`, `for 1h`, `for 90 min`, `for 2 hours`.
  - All-day: `all day`, or a date with no time.
  - Location: text after ` at ` or ` @ ` that is not a time (e.g. `at Cafe Rio`).
  - Calendar: `#name` (prefix match on calendar name; writable calendars only). Match order: the default calendar, then calendars in the active set in sidebar order, then the rest; first match wins. `#account/name` picks a calendar in a named account.
  - Recurrence: `every day`, `every <weekday>`, `every week`, `every month`, `weekdays`.
  - Task: a leading `task ` or `todo ` creates a task with the parsed date as due date, in the default task list (or `#list`).
- R3. All remaining text becomes the title.
- R4. As you type, a preview shows what the parser understood: highlighted tokens plus resolved fields (e.g. "Fri 6 Nov, 13:00–14:00 · Cafe Rio · Work"). Enter saves; Ctrl+Enter or "More details" opens the full editor pre-filled. The preview always shows the target calendar, and shows an error instead of saving when the title is empty. If the item lands in a calendar hidden by the active set, a toast says "Added to <calendar> (hidden in this set)" with a Show button.
- R5. Defaults: no date → today (or the slot clicked in the quick-create popup; typed date and time override the slot); in the Tasks view, text without a prefix creates a task; time with no end → default duration of 60 minutes; no `#` → default calendar ([account-management](account-management.md) R9); event timezone → system timezone.
- R6. Ambiguity rules are fixed and documented:
  - times win over location (`at 3pm` is a time);
  - a time with no date means today, even if that time has passed;
  - a weekday name means its next occurrence, or today if it is today.
- R7. The parser is a pure function with unit tests covering every token in R2, including en-AU and en-US date orders.

## Out of scope

- AI refinement: see [nl-quick-add](nl-quick-add.md). Languages other than English.
- Attendees/invitees in quick add.
- Complex recurrence (`every 2nd Tuesday`, `until`, counts) — use the full editor.

## Acceptance criteria

- [ ] "Lunch with Sam fri 1pm at Cafe Rio #Personal" creates a Friday 13:00–14:00 event titled "Lunch with Sam" at "Cafe Rio" in the Personal calendar.
- [ ] "task submit report tomorrow" creates a task due tomorrow.
- [ ] "Standup weekdays 9:15 for 15m" creates a weekday-recurring 15-minute event.
- [ ] Works with the network off.

## Dependencies

- [calendar-views](calendar-views.md), [tasks](tasks.md), [recurring-events](recurring-events.md), [tray-flyout](tray-flyout.md), [timezone-handling](timezone-handling.md)
