---
description: Typed quick add that parses English phrases like "Lunch with Sam fri 1pm at Cafe Rio" into an event or task, offline and rule-based
when-to-use: Before working on the quick-add box, its parser, or quick-create flows
tags: [ui, quick-add, parsing, events, tasks]
status: closed
---

# Quick add

> Updated on 2026-11-12: range phrases (this/next week, this/next month) added, and the parser reports phrases for [search-date-chips](search-date-chips.md).

## Summary

A single text box turns a typed phrase into an event (or a task) with date, time, duration, location and calendar filled in. Parsing is deterministic and rule-based, written in-house and run locally. No AI service, token, network call or third-party NLP package is involved, so it works offline.

## Requirements

- R1. Available in: the quick-create popup ([calendar-views](calendar-views.md) R5) and the [tray-flyout](tray-flyout.md).
  - Quick-create popup: clicking or dragging an empty slot in Week, Month or Agenda opens a small popup anchored to the slot, with the quick-add box pre-set to that slot and the calendar shown. A Month day, the all-day lane or an Agenda day pre-sets an all-day date; a typed time makes it a timed event. Esc or clicking outside closes it without saving. This replaces opening the full editor on a slot click.
  - Tray flyout: comes with [tray-flyout](tray-flyout.md); not built with this requirement.
  - Toolbar: removed 2026-11-12 (user's choice: a slot click is enough). Ctrl+N presses New again: New task in the Tasks view, else New event ([keyboard-navigation](keyboard-navigation.md)).
- R2. Recognised tokens (English, case-insensitive):
  - Dates: `today`, `tomorrow`, weekday names (next occurrence), `next <weekday>`, `in <n> days/weeks`, `5 Dec` / `Dec 5`, numeric dates in the Windows short-date order (e.g. `5/12` = 5 December for en-AU).
  - Ranges: `this week`, `next week`, `this month`, `next month`: an event goes on the first day of the range, or today if the range has already started; a task is due on the last day of the range (week end from the week-start setting). With no time an event is all-day; with a time it is timed. `last week` and `last month` are not dates in quick add and stay in the title.
  - Times: `1pm`, `1:30pm`, `13:00`, `noon`, `midnight`; ranges `1-2pm`, `1pm to 3pm`, `9:00-10:30`.
  - Duration: `for 30m`, `for 1h`, `for 90 min`, `for 2 hours`.
  - All-day: `all day`, or a date with no time.
  - Location: text after ` @ `, or after ` at ` when the next word starts with a capital letter or a digit and is not a time (`at Cafe Rio`, `at 12 Main St`). `look at budget` keeps "at budget" in the title. The location runs to the next recognised token or the end.
  - Calendar: `#name` (prefix match on calendar name; writable calendars ticked in the sidebar only, per [calendar-views](calendar-views.md) R6e). Match order: the default calendar, then the other ticked calendars in sidebar order; first match wins. With no `#`, the default calendar is used if ticked, else the first ticked writable calendar; if no writable calendar is ticked, the default calendar (else the first writable one) is used and the hidden banner (R4) shows. `#account/name` picks a calendar in a named account, and is the only form that can also reach a writable calendar unticked in the sidebar (ticked ones first).
  - Recurrence: `every day`, `every <weekday>`, `every week`, `every month`, `weekdays`.
  - Task: a leading `task ` or `todo ` creates a task with the parsed date as due date, in the default task list (or `#list`). Any writable task list can receive tasks: Microsoft To Do, and Radicale from [tasks](tasks.md) slice 3 (updated 2026-11-12; until then To Do only); `#` on a task matches writable task lists only. With no `#` the default task list is used even when unticked (else the first ticked list, else the first list). For a task the date, `#list` and recurrence (`every day`, `every <weekday>`, `every week`, `every month`, `weekdays`) are taken from the phrase ([tasks](tasks.md) R7b); times, durations and location words stay in the title. With a repeat and no typed date, the due date is the first matching day (today if it matches), so `task x every Fri` is a task "x" due on the next Friday, repeating weekly on Friday.
- R3. All remaining text becomes the title.
- R4. As you type, a preview under the box shows what the parser understood as a row of field chips (e.g. "Fri 6 Nov · 13:00–14:00 · Cafe Rio · Work · Every weekday"). Words inside the box are not highlighted (a WPF text box cannot colour parts of its text). Enter saves; Ctrl+Enter or "More details" opens the full editor pre-filled. The preview always shows the target calendar, and shows an error instead of saving when the title is empty. If the item lands in a calendar hidden in the sidebar (only possible via `#account/name`, a task going to an unticked To Do list, or no writable calendar being ticked), the in-app banner says "Added to <calendar> (hidden)" with a Show button.
  - R4a. Suggestions: a list under the box offers completions for the word at the caret. Tab inserts the highlighted item (the top one unless Up/Down moved). Enter saves, unless Up/Down has moved through the list, in which case it inserts the highlighted item. Esc closes the list only. A word the parser already recognises (`fri`, `wed`, `mon`) is not offered. Suggestions are computed by a pure function in `WindowsCalendar.Core` next to the parser, with unit tests.
    - Calendars: typing `#` lists matching writable calendars (task lists for a task), each with its colour marker and account name, narrowed by prefix as you type. Ticked ones come first in the R2 match order; unticked ones follow and insert `#account/name`. A ticked calendar is inserted as `#` and its full name with spaces removed (prefix match ignores spaces); if an earlier calendar in the match order would win that token (`#Work` next to an earlier `Workshop`), it is inserted as `#account/name` instead.
    - Dates and times: a partial word of at least 2 letters that starts a known phrase from R2 offers it, e.g. `tom` → tomorrow, `next f` → next friday, `fr` → friday, `no` → noon, `every m` → every monday / every month, `week` → weekdays. Only the fixed R2 vocabulary is offered, with at most 6 items; an exact, complete match is not offered again.
- R5. Defaults: no date → today (or the slot clicked in the quick-create popup; typed date and time override the slot); in the Tasks view, text without a prefix creates a task; time with no end → default duration of 60 minutes; no `#` → default calendar ([account-management](account-management.md) R9); event timezone → system timezone.
- R6. Ambiguity rules are fixed and documented:
  - times win over location (`at 3pm` is a time);
  - a time with no date means today, even if that time has passed;
  - a weekday name means its next occurrence, or today if it is today; `next <weekday>` means the one after that;
  - a day-month date (`5 Dec`, `5/12`) already past this year means next year;
  - a bare number is a time only after `at` (`at 3`) or in a range (`9-5`); with no am/pm, 1–6 means pm, 7–11 means am and 12 means noon (a range end takes the later reading, so `9-5` is 09:00–17:00); elsewhere a bare number stays in the title;
  - a weekday or month name counts as a date only when it stands alone as a word and is not followed by `'s`, so `Sun cream` and `May's party` stay in the title, but `lunch sun` is Sunday. Short forms that are common words (`sun`, `sat`, `wed`, `mar`, `may`) count only when they are the last word or are next to another date or time token;
  - a range whose end is before its start ends on the next day (`11pm-1am`);
  - a range wins over a duration (`1-2pm for 30m` is 13:00–14:00); the duration word is left in the title.
- R7. The parser is a pure function in `WindowsCalendar.Core` (phrase, now, short-date order, calendar and list names → result with each field and the text span it came from), with unit tests covering every token in R2 and every rule in R6, including en-AU and en-US date orders. The spans let [nl-quick-add](nl-quick-add.md) tell which words the rules used, and the parser reports each date phrase (kind and span) for [search-date-chips](search-date-chips.md).

## Out of scope

- AI refinement: see [nl-quick-add](nl-quick-add.md). Languages other than English.
- Attendees/invitees in quick add.
- Complex recurrence (`every 2nd Tuesday`, `until`, counts) — use the full editor.

## Acceptance criteria

- [x] "Lunch with Sam fri 1pm at Cafe Rio #Personal" creates a Friday 13:00–14:00 event titled "Lunch with Sam" at "Cafe Rio" in the Personal calendar.
- [x] "task submit report tomorrow" creates a task due tomorrow.
- [x] "Standup weekdays 9:15 for 15m" creates a weekday-recurring 15-minute event.
- [x] Works with the network off.
- [x] "look at budget tomorrow" creates an event titled "look at budget" with no location.
- [x] Clicking an empty Week slot opens the quick-create popup on that slot; Ctrl+Enter there opens the full editor pre-filled.
- [ ] Ctrl+N opens New event (New task in the Tasks view); the toolbar has no quick-add box.
- [x] Typing `#wo` lists the Work calendar; Tab inserts it. Typing `tom` offers "tomorrow".
- [ ] "task x every Fri" creates a task "x" due on the next Friday that repeats weekly on Friday in Microsoft To Do.
- [x] "Dentist next week" creates an all-day event on next week's first day; "Dentist this week" on today if the week has started.
- [x] "task Report next week" creates a task due on next week's last day (week end from the week-start setting).
- [x] "Review last week" creates an event titled "Review last week" with today's date.
- [x] The parser reports each date phrase with its kind and span, and every existing quick-add test phrase gives the same result as before.

## Dependencies

- [calendar-views](calendar-views.md), [tasks](tasks.md), [recurring-events](recurring-events.md), [tray-flyout](tray-flyout.md), [timezone-handling](timezone-handling.md)
