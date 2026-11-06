---
description: Tasks/to-dos from Radicale (VTODO) and Microsoft To Do, shown and edited in one tasks UI
when-to-use: Before working on task lists, task editing, or task sync mapping
tags: [tasks, vtodo, todo, ui]
status: closed
---

# Tasks

## Summary

Tasks from Radicale VTODO collections and Microsoft To Do lists appear in a unified Tasks view, and can be created, edited, completed and deleted offline. Tasks with a due date also appear on the calendar views. The backends differ in what they support, so the editor shows only the fields the item's backend supports.

## Requirements

- R1. Tasks view: the fifth view tab (Day | Week | Month | Agenda | Tasks, key K). Tasks are grouped by task list (account → list), with filters for All / Today / Overdue / Completed, and sorting by due date. The view follows the active calendar set ([calendar-sets](calendar-sets.md) R6). New tasks are added with quick add, which creates a task by default in this view ([quick-add](quick-add.md) R5). Opening a task uses the same right-side editor panel and auto-save as events ([calendar-views](calendar-views.md) R6b–R6d).
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
- R6. Tasks with a due date show in the Day/Week/Month views as a distinct checkbox item, toggleable in settings (default on). A date-only due task sits in the all-day lane; a task with a due time sits at that time. Completed tasks are hidden from the calendar views.
- R7. Recurring tasks: display and complete only (see [recurring-events](recurring-events.md) R8).
- R7a. Special To Do lists such as Flagged emails are read-only ([sync-ms365](sync-ms365.md) R9).
- R8. Subtasks/checklist items: displayed read-only in v1.
- R9. Unknown VTODO properties are preserved on edit (see [sync-radicale](sync-radicale.md) R8).

## Interim (first slice: Tasks view and Microsoft To Do writes)

- R1: Tasks is the fourth view tab (Week | Month | Agenda | Tasks); the K shortcut comes with [keyboard-navigation](keyboard-navigation.md). Filters: **All** (open tasks; the default), **Today** (open, due today), **Due in 7 days** (open, due today to 6 days ahead), **Overdue** (open, due before today), **Completed**. The tasks of all visible task lists form one list (unticked task lists are not shown), sorted by due date, undated ones last, then by title. Each row: completed checkbox, title, the task list (its shape marker and name, with the account in the tooltip), due date (in the error colour when overdue) and a "!" glyph for high importance.
- A "New task" box at the top of the view adds a task with that title to the default task list ([account-management](account-management.md) R9) when Enter is pressed. Quick add comes with [quick-add](quick-add.md).
- R1, R2, R5: clicking a task opens the task editor in the event editor's place, with the same auto-save. Fields: title, completed, due date (date only for To Do), importance (Low / Normal / High) and notes. The task list is shown but cannot be changed. Delete asks for confirmation, like events.
- View-only in the editor and calendar (the checkbox is disabled): Radicale tasks (their writes come later), Flagged emails tasks (R7a) and read-only lists. Recurring To Do tasks can only be completed or reopened (R7).
- R6: open tasks with a due date show in Week (all-day lane, or at the due time for a Radicale task with one), Month and Agenda as an item with a checkbox before the title in the list colour. Ticking it completes a To Do task without opening the editor; clicking elsewhere on the item opens the editor. Likewise in the Tasks view, the checkbox only completes or reopens, and clicking the rest of the row opens the editor. Task items cannot be dragged. Completed tasks are hidden from these views. The settings toggle comes with [settings-page](settings-page.md); until then they are always shown.
- Tasks are placed on their due date, not their start date.
- Undo for task changes, reminders (R2), checklist items (R8) and Radicale task writes come later.

## Out of scope

- Editing subtasks/checklist items.
- Editing recurrence rules for tasks.
- Moving tasks between accounts.
- Legacy Outlook tasks API (deprecated; To Do API only).

## Acceptance criteria

- [ ] Create/complete/delete a task on each backend, offline and online; the change is visible in Microsoft To Do / another CalDAV client.
- [ ] A task with a due date appears on the calendar on that date.
- [ ] Priority round-trips per the R4 mapping.

## Dependencies

- [sync-ms365](sync-ms365.md), [sync-radicale](sync-radicale.md), [local-store-offline](local-store-offline.md), [reminders-notifications](reminders-notifications.md)
