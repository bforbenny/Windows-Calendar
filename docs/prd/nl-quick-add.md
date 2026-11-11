---
description: On-device NPU model refines the quick-add rule parser so free English phrasing becomes an event or task
when-to-use: Before working on AI refinement of quick add, how model and rule results merge, or the AI-running indicator
tags: [quick-add, ai, npu, parsing, events, tasks]
status: pending
---

# Natural-language quick add (NPU)

## Summary

The [quick-add](quick-add.md) rule parser stays the instant path and drives the live preview on every keystroke. When the rules leave date-like words unexplained, the on-device model ([on-device-model](on-device-model.md)) reads the whole phrase after a short pause and refines the preview, so phrasing the rules do not know ("the Tuesday after next", "2nd Thu of Dec", "around 3ish") still works. Code, not the model, resolves and validates every value. Without the model, quick add behaves exactly as [quick-add](quick-add.md).

## Requirements

- R1. Applies in every quick-add entry point ([quick-add](quick-add.md) R1).
- R2. When the model runs: only if the rule result leaves date- or time-like words in the title (weekday or month names, ordinals, "after", "before", "next", "this", "week", "month", "around", "ish", "end of", "morning", "afternoon", "evening", "tonight"), or no date was found while such words are present. Otherwise the rule result is final and no model call is made.
- R3. Trigger: after ~600 ms without typing, and on Enter if it has not run for the current text. Typing cancels a pending or running request and the preview reverts to the rule result until a new model result arrives. Results are cached per (phrase, date) for the session.
- R4. Indicator: while the model runs, the input border shows a rotating hue, and screen readers hear "Refining". When Windows "Animation effects" is off, a static accent border is shown; in High Contrast, the system highlight colour is used. The indicator stops when the request finishes, fails or is cancelled.
- R5. Enter:
  - when the model is not needed (R2) or unavailable, saves at once;
  - otherwise waits up to 3 s for a running request; typing during the wait cancels it and does not save;
  - if the merged result equals the rule result (R7), saves;
  - if the model changed any field, updates the preview with changed fields highlighted, and only an Enter pressed after that update is shown saves (an Enter queued during the wait does not count);
  - on timeout or failure, saves the rule result.
  Ctrl+Enter opens the full editor pre-filled with the preview as shown ([quick-add](quick-add.md) R4).
- R6. Model output is a flat JSON object: kind (event/task), title, relative date (mode none / day offset / weekday + weeks ahead / nth weekday of month / month-day, with its numbers), start and end as "HH:mm" or "", duration in minutes, all-day, location, calendar hint, repeat (none/daily/weekdays/weekly/monthly), importance (low/normal/high). Schema limits per [on-device-model](on-device-model.md) R1.
- R7. Merge rules:
  - a field taken from a token the rules matched (numeric dates, explicit times, `#calendar`, `task` prefix, duration, recurrence) keeps the rule value; the model fills only fields the rules left empty or defaulted, and may shorten the title by removing words it used;
  - fields are compared after normalising case and whitespace; a difference only in title case or spacing is not a change;
  - code turns the relative date into a real date in the system time zone, rejects impossible dates and times, and resolves the calendar hint with the `#name` match order ([quick-add](quick-add.md) R2);
  - an invalid or ungrounded field ([on-device-model](on-device-model.md) R6, R7) is dropped, keeping the rule value.
- R8. Tasks use only the date (as due date), title, list and importance; start, end, duration and repeat are ignored. Recurrence is limited to the quick-add set; attendees are never set.
- R9. Preview highlighting: text fields from the model are highlighted by locating them in the input; model-resolved dates and times are marked as coming from the model instead of highlighting words.
- R10. Eval: a phrase set of at least 150 entries (about 50 outside the quick-add grammar, including en-AU and en-US numeric dates and DST-boundary dates) with a fixed reference time and expected fields, run with the [on-device-model](on-device-model.md) R11 harness.

## Out of scope

- Editing or moving existing items by phrase ("move dentist to Thu").
- Attendees, reminders, time zones named in the phrase ("3pm London time"), complex recurrence.

## Acceptance criteria

- [ ] "Coffee with Sam the Tuesday after next around 3ish" shows the correct Tuesday at 15:00 after the pause, with the changed fields highlighted; Enter then saves.
- [ ] "Lunch with Sam fri 1pm at Cafe Rio" makes no model call and saves on the first Enter.
- [ ] "5/12 standup" keeps 5 December in en-AU even if the model says otherwise.
- [ ] Two quick Enters during the wait do not save a model change the user has not seen.
- [ ] "task call bank urgent tomorrow" creates a high-importance task due tomorrow.
- [ ] With AI off or unavailable, behaviour is identical to [quick-add](quick-add.md), with no indicator and no delay.
- [ ] The indicator follows the animation and High Contrast settings and is announced to screen readers.
- [ ] On the phrase set: at least 90% exact match on all fields for the out-of-grammar subset, no benign phrase blocked by moderation, no invalid date or time saved.
- [ ] Warm Enter-to-preview at most 1.5 s at p95; cold at most 5 s.

## Dependencies

- [on-device-model](on-device-model.md), [quick-add](quick-add.md) (rule parser and preview must exist first), [timezone-handling](timezone-handling.md), [ui-style](ui-style.md) (motion)
