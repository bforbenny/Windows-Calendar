---
description: Layout and contents of the Settings page, which gathers every user setting in one place
when-to-use: Before adding or moving any user-facing setting, or working on the Settings UI
tags: [ui, settings]
status: pending
---

# Settings page

## Summary

All user settings live on one Settings page inside the main window, laid out like Windows Settings: a section list on the left and the section's settings on the right. Each setting is owned by its feature PRD; this PRD only fixes where it appears.

## Requirements

- R1. Opened with Ctrl+, or the toolbar/menu; replaces the calendar area in the main window, with a Back button (and Esc) to return to the previous view.
- R2. Sections and contents:

  | Section | Settings | Owning PRD |
  | --- | --- | --- |
  | Accounts | add / remove account, display name, email address (Radicale), status and last sync time, sign in again | [account-management](account-management.md) |
  | Calendars | per calendar/task list: colour, shape, reminders on, default reminder, default calendar, default task list, holiday calendar, "Show as shading only". Visibility is set in the sidebar and calendar sets | [calendar-views](calendar-views.md), [account-management](account-management.md), [ui-colour-theme](ui-colour-theme.md), [calendar-shading](calendar-shading.md), [reminders-notifications](reminders-notifications.md) |
  | Calendar sets | create, rename, reorder, delete sets | [calendar-sets](calendar-sets.md) |
  | Appearance | theme (Light / Dark / Use Windows setting), show calendar shapes, week numbers, first day of week, show due tasks on calendar, secondary timezone | [app-platform](app-platform.md), [ui-colour-theme](ui-colour-theme.md), [calendar-shading](calendar-shading.md), [tasks](tasks.md), [timezone-handling](timezone-handling.md) |
  | Working hours | working days, start and end time | [calendar-shading](calendar-shading.md) |
  | Startup and tray | start with Windows | [app-platform](app-platform.md) |
  | Keyboard shortcuts | read-only shortcut list (same as the cheat sheet) | [keyboard-navigation](keyboard-navigation.md) |
  | About | version, open log folder, what is kept and what is lost (R7) | [app-platform](app-platform.md), [app-updates](app-updates.md) |

- R3. Settings apply immediately (no Save button), consistent with the editor's auto-save.
- R4. Destructive settings (remove account, delete calendar set) ask for confirmation (CLAUDE.md security rule 5).
- R5. Settings are stored locally only (LocalSettings and the local store, not roamed); nothing on this page is written to the servers except account sign-in.
- R6. At window width below 1200 epx the section list collapses to a drop-down or icon rail ([display-scaling](display-scaling.md) R4).
- R7. About states plainly: uninstalling deletes unsynced changes, conflict copies, colours, shapes, calendar sets and settings; only what is on the servers is kept. Reminders keep firing for up to 48 hours after Quit ([reminders-notifications](reminders-notifications.md) R2).

## Out of scope

- Search within settings.
- Import/export or sync of settings between PCs.

## Acceptance criteria

- [ ] Every user setting named in the PRDs is reachable from this page, in the section listed in R2.
- [ ] Changing the theme or week numbers takes effect immediately without a restart.
- [ ] Removing an account asks for confirmation.

## Dependencies

- [app-platform](app-platform.md), [account-management](account-management.md), [ui-style](ui-style.md), [display-scaling](display-scaling.md)
