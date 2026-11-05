---
description: Agenda/list view showing upcoming occurrences as a scrolling list grouped by day
when-to-use: Before working on the Agenda view or list rendering of occurrences
tags: [ui, views, agenda]
status: pending
---

# Agenda view

## Summary

A fourth view, alongside Day/Week/Month, lists events and due tasks as a continuous list grouped by day. It is the quickest way to scan what is coming up.

## Requirements

- R1. Agenda is selectable from the view switcher and with the A key.
- R2. Opens scrolled to today, with infinite scroll forward and backward, limited to data in the local store (the MS365 sync window; full collections for Radicale).
- R3. Day headers (e.g. "Thu 5 Nov 2026", with "Today" / "Tomorrow" labels and the week number when enabled, see [calendar-shading](calendar-shading.md)). Days with no items are skipped. Public holidays show in the day header.
- R4. Row content: time range or "All day", calendar colour, title, location, show-as style (see [event-show-as](event-show-as.md)), recurrence icon, and Join button when applicable (see [online-meeting-join](online-meeting-join.md)).
- R5. Multi-day events appear on each day they span, labelled "Day n of m".
- R6. Tasks with a due date appear under their day, with a checkbox, when the tasks-on-calendar setting is on ([tasks](tasks.md) R6).
- R7. Respects calendar visibility and the active calendar set ([calendar-sets](calendar-sets.md)).
- R8. Same item actions as other views: open, edit, delete, duplicate, undo, keyboard navigation (Up/Down between rows, Enter to open).
- R9. Performance: initial render < 200 ms; scrolling loads further days without visible stalls.

## Out of scope

- Drag-and-drop in the Agenda view.
- Year view and custom N-day views.

## Acceptance criteria

- [ ] Agenda shows events from MS365 and Radicale in time order, grouped by day, starting at today.
- [ ] Empty days are not shown; multi-day events show on each day.
- [ ] Works offline.

## Dependencies

- [calendar-views](calendar-views.md), [local-store-offline](local-store-offline.md), [tasks](tasks.md), [calendar-sets](calendar-sets.md)
