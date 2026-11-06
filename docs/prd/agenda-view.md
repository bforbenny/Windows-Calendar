---
description: Agenda view showing occurrences as day columns of plain lists, scrolling horizontally and vertically
when-to-use: Before working on the Agenda view or list rendering of occurrences
tags: [ui, views, agenda]
status: in-progress
---

# Agenda view

## Summary

A view alongside Week and Month that shows one column per day, each a plain list of that day's events and due tasks (not placed on a time grid). It is the quickest way to scan what is coming up.

## Requirements

- R1. Agenda is selectable from the view switcher and with the A key.
- R2. Seven day columns fill the width. It opens with today as the first column and scrolls naturally ([calendar-views](calendar-views.md) R1a): horizontally through days, backward and forward, and vertically through long lists, within the local store's occurrence window. Day headers stay at the top. Today scrolls back to today.
- R3. Each column has a day header (e.g. "Thu 5 Nov", with "Today" / "Tomorrow" labels and the week number when enabled, see [calendar-shading](calendar-shading.md)). Empty days keep their column and show "Nothing scheduled". Public holidays show in the day header. In each list, all-day items come first, then timed items by start time.
- R4. Row content: time range or "All day", calendar colour, title, location, show-as style (see [event-show-as](event-show-as.md)), recurrence icon, and Join button when applicable (see [online-meeting-join](online-meeting-join.md)).
- R4a. Rows have no fixed height: each card grows to fit its content, with the title wrapping onto as many lines as it needs and the time and location (or "Day n of m") below.
- R5. Multi-day events appear on each day they span, labelled "Day n of m".
- R6. Tasks with a due date appear under their day, with a checkbox, when the tasks-on-calendar setting is on ([tasks](tasks.md) R6).
- R7. Respects calendar visibility and the active calendar set ([calendar-sets](calendar-sets.md)).
- R8. Same item actions as other views: open, edit, delete, duplicate, undo, keyboard navigation (Up/Down between rows, Enter to open), and drag to another day keeping the time ([calendar-views](calendar-views.md) R7).
- R9. Performance: initial render < 200 ms; scrolling loads further days without visible stalls.

## Out of scope

- Drag-and-drop in the Agenda view.
- Year view and custom N-day views.

## Acceptance criteria

- [ ] Agenda shows events from MS365 and Radicale in time order, one column per day, starting at today.
- [ ] Empty days show "Nothing scheduled"; multi-day events show on each day.
- [ ] Works offline.

## Dependencies

- [calendar-views](calendar-views.md), [local-store-offline](local-store-offline.md), [tasks](tasks.md), [calendar-sets](calendar-sets.md)
