---
description: Tasks/to-dos from Radicale (VTODO) and Microsoft To Do, shown and edited in one tasks UI
when-to-use: Before working on task lists, task editing, or task sync mapping
tags: [tasks, vtodo, todo, ui]
status: pending
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
