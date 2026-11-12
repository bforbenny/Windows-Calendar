---
description: Tasks/to-dos from Radicale (VTODO) and Microsoft To Do, shown and edited in one tasks UI
when-to-use: Before working on task lists, task editing, or task sync mapping
tags: [tasks, vtodo, todo, ui]
status: in-progress
---

# Tasks

## Summary

Tasks from Radicale VTODO collections and Microsoft To Do lists appear in a unified Tasks view, and can be created, edited, completed and deleted offline. Tasks with a due date also appear on the calendar views. The backends differ in what they support, so the editor shows only the fields the item's backend supports.

## Requirements

- R1. Tasks view: the fourth view tab (Week | Month | Agenda | Tasks, key F4, [keyboard-navigation](keyboard-navigation.md) R2). Tasks are grouped by task list (account → list), with filters for All / Today / Overdue / Completed, and sorting by due date. The view follows the active calendar set ([calendar-sets](calendar-sets.md) R6). New tasks are added with quick add, which creates a task by default in this view ([quick-add](quick-add.md) R5). Opening a task uses the same right-side editor panel and auto-save as events ([calendar-views](calendar-views.md) R6b–R6d).
- R2. Common fields: title, notes, due date (optional time), completed, importance/priority, reminder.
- R2a. Due dates are date-only unless a time is set, and date-only due dates never shift with the timezone (like all-day events, [timezone-handling](timezone-handling.md) R1). For To Do, the date part of `dueDateTime` is used.
- R3. Field mapping:

  | Field | VTODO | Graph todoTask |
  | --- | --- | --- |
  | Title | SUMMARY | title |
  | Notes | DESCRIPTION | body (text) |
  | Due | DUE | dueDateTime |
  | Start | DTSTART | startDateTime |
  | Completed | STATUS=COMPLETED + COMPLETED | status=completed + completedDateTime |
  | Priority | PRIORITY 1-9 | importance low/normal/high |
  | Reminder | VALARM | reminderDateTime + isReminderOn |
  | Recurrence | RRULE | recurrence |
  | Subtasks | RELATED-TO | checklistItems (displayed only) |

- R4. Priority mapping: VTODO 1-4 → high, 5 / 0 → normal, 6-9 → low.
- R5. Fields that a backend does not support are hidden in the editor for that task (e.g. no percent-complete for To Do).
- R6. Tasks with a due date show in the Week/Month/Agenda views as a distinct checkbox item, toggleable in settings (default on). A date-only due task sits in the all-day lane; a task with a due time sits at that time. Completed tasks are hidden from the calendar views.
- R7. Recurring tasks: completing advances to the next instance per server behaviour ([recurring-events](recurring-events.md) R8). Radicale does not advance a repeating VTODO itself, so the app does (added 2026-11-12): completing a repeating Radicale task moves DUE (and DTSTART, keeping their distance) to the next occurrence of its RRULE after the current due date and keeps it open, as Tasks.org and DAVx5 do; when the rule has no next occurrence, the task is completed.
- R7b. Repeat on Microsoft To Do tasks (reopened 2026-11-11): the task editor shows the event editor's Repeat panel (Doesn't repeat / Daily / Weekly on chosen days / Monthly / Yearly, interval, ends) under the due date, mapped to the todoTask `recurrence` (patternedRecurrence: pattern from the rule, range start = the due date). Setting a repeat on a task with no due date sets the due date to today, or to the first chosen weekday for Weekly. Changing the rule applies to the task as a whole; there is no this-and-following for tasks. Radicale task rules stay view-only. Quick add can set a repeat on a task ([quick-add](quick-add.md) R2).
- R7a. Special To Do lists such as Flagged emails are read-only ([sync-ms365](sync-ms365.md) R9).
- R8. Subtasks/checklist items: displayed read-only in v1.
- R11. Task list field (added 2026-11-12): the task editor has a List drop-down, for new and existing tasks, listing every writable task list (To Do and Radicale) with its account. A move queues a create in the new list and a delete in the old one as one linked outbox group ([calendar-views](calendar-views.md) R8 pattern). It first asks "Move to <list>?" with what the task can't take along whenever anything would be lost, within the account too; a move from Microsoft To Do always lists "Checklist steps, files and linked items, if the task has any" (sync does not download them, so the app can't tell), so every move from To Do asks. The drop-down acts only on a committed choice (closing it on an item), never on arrow keys while it is closed. Mapped across accounts: title, notes, due date (a Radicale due time is lost going to To Do), completed, importance (R4), first reminder and the repeat rule where the Repeat panel can express it (else the move is refused with a message). Lost to To Do: due time, extra alarms, percent-complete, categories, skipped dates and changed occurrences of a repeat, and other VTODO properties. Lost to Radicale: checklist items, linked resources. Moves are not undoable yet. A Microsoft To Do task gets a new id when it moves; a Radicale task keeps its UID.
- R12. The Tasks view's "New task" box has a list drop-down next to it (writable task lists, with account), remembered across sessions; it starts on the default task list. `#list` typed in the text overrides it.
- R9. Unknown VTODO properties are preserved on edit (see [sync-radicale](sync-radicale.md) R8).
- R10. Delete old completed tasks (added 2026-11-12): with the Completed filter on, the Tasks view shows a "Delete completed tasks older than 1 year (N)" link when N > 0. It deletes, on the server, the completed tasks in the visible writable task lists whose completion time (`completedDateTime` / COMPLETED, else last modified) is more than 365 days ago. Read-only lists and repeating tasks are skipped. A confirmation dialog lists the count per task list (account in brackets) with a red Delete button; there is no undo. The deletes go through the outbox like any task delete, so they can still be discarded from the pending changes list ([ui-states](ui-states.md)) until they are sent.

## Interim (first slice: Tasks view and Microsoft To Do writes)

- R1: Tasks is the fourth view tab (Week | Month | Agenda | Tasks); F4 switches to it ([keyboard-navigation](keyboard-navigation.md)). Filters: **All** (open tasks; the default), **Today** (open, due today), **Due in 7 days** (open, due today to 6 days ahead), **Overdue** (open, due before today), **Completed**. The tasks of all visible task lists form one list (unticked task lists are not shown), sorted by due date, undated ones last, then by title. Each row: completed checkbox, title, the task list (its shape marker and name, with the account in the tooltip), due date (in the error colour when overdue) and a "!" glyph for high importance.
- A "New task" box at the top of the view adds a task to the default task list ([account-management](account-management.md) R9) when Enter is pressed. Second slice: the text goes through the quick-add parser as a task ([quick-add](quick-add.md)), so a date, `!` importance and repeat in the text are applied, and the "Task added · Undo" banner shows. With no writable To Do list the box is disabled; if creating fails, the text is kept and the banner says why.
- New task from the toolbar's New button ([calendar-views](calendar-views.md) interim) opens the task editor on a blank task in the default task list (or the first writable To Do list). It is created once it has a title; closing the panel while the title is empty discards it. With no writable To Do list, New task is disabled.
- Until a new event or task is created, the panel heading is a drop-down ("New event" / "New task") that switches to the other kind, keeping the typed title; the other fields start from their defaults.
- R1, R2, R5: clicking a task opens the task editor in the event editor's place, with the same auto-save. Fields: title, completed, due date (date only for To Do; an optional time for Radicale from slice 3), Repeat (R7b), reminder ([reminders-notifications](reminders-notifications.md) R5a), importance (Low / Normal / High) and notes. The task list is shown but cannot be changed. Delete asks for confirmation, like events.
- View-only in the editor and calendar (the checkbox is disabled): Radicale tasks (their writes come later), Flagged emails tasks (R7a) and read-only lists. Repeating To Do tasks whose pattern the Repeat panel can't show can only be completed or reopened (R7).
- R6: open tasks with a due date show in Week (all-day lane, or at the due time for a Radicale task with one), Month and Agenda as an item with a checkbox before the title in the list colour. Ticking it completes a To Do task without opening the editor; clicking elsewhere on the item opens the editor. Likewise in the Tasks view, the checkbox only completes or reopens, and clicking the rest of the row opens the editor. Task items cannot be dragged. Completed tasks are hidden from these views. The settings toggle comes with [settings-page](settings-page.md); until then they are always shown.
- Tasks are placed on their due date, not their start date.
- Undo for task edits and deletes, checklist items (R8), the R6 settings toggle and Radicale task writes come later.

## Interim (second slice: task editing safety, undo, cleanup, added 2026-11-12)

- Typed text is kept: a reload of the open task after a sync or a change elsewhere does not overwrite the title or notes while either has keyboard focus, as in the event editor ([calendar-views](calendar-views.md) R6d).
- Empty title: clearing the title keeps the stored title, and the box shows it again after the save.
- Clearing the due date is not offered while a repeat is set (the Clear button is disabled, with the tooltip "Set Doesn't repeat first"); otherwise the repeat would silently restart from today.
- Save failures: if the task was deleted meanwhile the panel closes; if a save fails, a warning notice shows in the panel, as in the event editor. Errors are logged without task contents.
- View-only and complete-only tasks show what they have, disabled rather than hidden: the Repeat panel with the task's rule, the reminder, and a Radicale task's due time.
- Undo ([undo](undo.md)): ticking a task complete or open from the Tasks view, the calendar or the tray shows "Task completed · Undo" / "Task reopened · Undo" for 8 seconds, and Ctrl+Z undoes it. Undo restores the task's previous payload through the task mapper, coalescing with the unsent change, and is refused if the task changed on the server (undo R6). Undoing a quick-added task closes the task editor if it shows that task.
- Keyboard: each Tasks view row can take focus with Tab; Enter opens the task and Space ticks it.
- Conflicts on a task ([conflict-resolution](conflict-resolution.md)) list the task fields that differ (title, completed, due date, importance, notes, repeat, reminder), not just the title.
- R10 (delete old completed tasks) is built for Microsoft To Do; Radicale lists join with Radicale task writes (slice 3).
- Optimisation: a task's backend is decided by its account type, not by the shape of the stored data; importance is read once per task version rather than on every redraw.


## Interim (third slice: Radicale task writes, list field, added 2026-11-12)

- Radicale tasks become editable like To Do tasks: create, edit, complete, reopen and delete, queued in the outbox and sent as CalDAV PUT (with If-Match) and DELETE, like Radicale events ([sync-radicale](sync-radicale.md)). The stored VTODO is edited in place with Ical.Net and re-serialised, keeping unknown properties (R9, sync-radicale R8).
- Fields for Radicale tasks: title (SUMMARY), notes (DESCRIPTION), due date with an optional time (DUE as a DATE, or a DATE-TIME in the Windows timezone; a task with DTSTART keeps the same distance to DUE), importance (R4: High writes 1, Normal 5, Low 9; an existing PRIORITY already in the chosen band is kept), completed (STATUS=COMPLETED, COMPLETED, PERCENT-COMPLETE:100; reopening sets STATUS=NEEDS-ACTION and removes COMPLETED and PERCENT-COMPLETE) and reminder (the first VALARM, as for Radicale events; other alarms are kept). Repeat is shown disabled: Radicale task rules stay view-only in the editor.
- New Radicale tasks get a new UID, DTSTAMP, CREATED and LAST-MODIFIED; every edit updates LAST-MODIFIED and DTSTAMP and increments SEQUENCE.
- R7 for Radicale: ticking a repeating Radicale task advances it (R7) and shows "Task advanced to <date> · Undo"; Undo puts the previous dates back.
- Undo for ticks, the conflict field list and R10 (delete old completed) cover Radicale tasks too.
- Quick add and the New task box can create tasks in Radicale lists; `#` on a task matches every writable task list. A repeat phrase on a Radicale task writes an RRULE ([quick-add](quick-add.md)).
- R11 and R12 are built.
- Not in this slice: editing a Radicale task's repeat rule in the editor, checklist items, undo of moves.

## Out of scope

- Editing subtasks/checklist items.
- Editing recurrence rules for Radicale tasks (VTODO RRULE) in the editor.
- Legacy Outlook tasks API (deprecated; To Do API only).

## Acceptance criteria

- [ ] Create/complete/delete a task on each backend, offline and online; the change is visible in Microsoft To Do / another CalDAV client.
- [ ] A task with a due date appears on the calendar on that date.
- [ ] Priority round-trips per the R4 mapping.
- [ ] Setting Weekly on Friday on a To Do task in the editor shows it repeating in Microsoft To Do; completing it there or here brings the next Friday's instance after sync.
- [ ] Changing a repeating To Do task back to "Doesn't repeat" removes the repeat in Microsoft To Do.
- [ ] A sync while typing notes in the task editor leaves the typed text in place.
- [ ] Ticking a task on the calendar shows "Task completed · Undo"; Undo brings it back open, and Microsoft To Do agrees after sync.
- [ ] Create, edit (title, notes, due date and time, importance, reminder), complete and delete a Radicale task; another CalDAV client (DAVx5 / Thunderbird) shows the change after sync, and properties the app doesn't know survive.
- [ ] Ticking a weekly repeating Radicale task moves it to next week's date and keeps it open; Undo puts it back.
- [ ] Moving a task between two To Do lists, between two Radicale lists, and To Do ⇄ Radicale (with the confirmation listing what's lost) leaves exactly one copy in the target list after sync; a weekly repeat survives the move.
- [ ] The New task box's list drop-down is remembered after restarting the app; `#list` in the text overrides it.
- [ ] "Delete completed tasks older than 1 year" shows the right count, asks for confirmation, and the tasks are gone from Microsoft To Do after sync; newer completed, open, repeating and Flagged emails tasks are untouched.

## Open questions

- R7b: does Graph accept and keep `endDate` and `numbered` recurrence ranges on todoTask? If To Do keeps only `noEnd`, the panel hides "Ends" for tasks. Check against a real To Do list before building.

## Dependencies

- [sync-ms365](sync-ms365.md), [sync-radicale](sync-radicale.md), [local-store-offline](local-store-offline.md), [reminders-notifications](reminders-notifications.md)
