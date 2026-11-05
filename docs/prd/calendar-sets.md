---
description: Named calendar sets that switch which calendars and task lists are visible in one click (e.g. Work, Personal)
when-to-use: Before working on calendar visibility, the sidebar, or set switching
tags: [ui, calendars, visibility, sets]
status: pending
---

# Calendar sets

## Summary

A calendar set is a named group of calendars and task lists, which can span accounts. Switching to a set shows only its members, so you can move between contexts such as Work, Personal or Everything with one click.

## Requirements

- R1. A built-in "All" set shows every calendar; it cannot be deleted, is always first in the selector, and is Ctrl+1.
- R2. User-defined sets: create, rename, reorder, delete; each set holds a list of collections (calendars and task lists) from any account.
- R3. "Save current visibility as set…" creates a set from the sidebar's current checkboxes.
- R4. Switching: a set selector at the top of the sidebar, and Ctrl+1 … Ctrl+9 for the first nine sets ([keyboard-navigation](keyboard-navigation.md)).
- R5. Switching a set changes view visibility only. Ticking or unticking a calendar while a set is active temporarily overrides it until the next switch, and the selector shows "(modified)". The sidebar checkbox is the only per-calendar visibility control; overrides and "(modified)" persist across restarts.
- R6. The active set applies to Day/Week/Month/Agenda/Tasks, the [tray-flyout](tray-flyout.md), holiday shading ([calendar-shading](calendar-shading.md)) and the default filter for search.
- R7. Reminders are not affected by sets: reminders fire for all calendars that have reminders enabled.
- R8. Sets and the active set are stored locally (not synced) and persist across restarts. Removing an account removes its collections from all sets.
- R9. Collections added later (a new account, or a calendar created on the server) join only "All" ([account-management](account-management.md) R11).
- R10. A new event or task created in a calendar the active set hides triggers the "hidden in this set" toast ([quick-add](quick-add.md) R4).

## Out of scope

- Syncing sets between devices.
- Automatic switching (by time of day or Windows Focus).

## Acceptance criteria

- [ ] A "Work" set containing two MS365 calendars and one Radicale calendar shows only those three when selected.
- [ ] Ctrl+2 switches to the second set.
- [ ] Reminders from a calendar outside the active set still fire.

## Dependencies

- [calendar-views](calendar-views.md), [account-management](account-management.md), [keyboard-navigation](keyboard-navigation.md)
