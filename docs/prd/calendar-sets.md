---
description: Named calendar sets that switch which calendars and task lists are visible in one click (e.g. Work, Personal)
when-to-use: Before working on calendar visibility, the sidebar, or set switching
tags: [ui, calendars, visibility, sets]
status: in-progress
---

# Calendar sets

## Summary

A calendar set is a named group of calendars and task lists, which can span accounts. Switching to a set shows only its members, so you can move between contexts such as Work, Personal or Everything with one click.

## Requirements

- R1. A built-in "All" set shows every calendar; it cannot be deleted, is always first in the selector, and is Ctrl+1.
- R2. User-defined sets: create, rename, reorder, delete; each set holds a list of collections (calendars and task lists) from any account.
- R3. Members are set from the sidebar. The selector's ⋯ menu has "Save as new set…" (asks for a name, then saves the current ticks as a new set), "Update '<set>'" (shown while a user-defined set is "(modified)"; saves the current ticks into it) and "Manage sets…" (opens Settings > Calendar sets). Settings > Calendar sets renames, reorders (up / down) and deletes sets; it has no member checklist. Deleting asks first; deleting the active set switches to "All".
- R4. Switching: a set selector at the top of the sidebar, and Ctrl+1 … Ctrl+9 for the first nine sets ([keyboard-navigation](keyboard-navigation.md)).
- R5. Switching a set changes view visibility only. Ticking or unticking a calendar while a set is active temporarily overrides it until the next switch, and the selector shows "(modified)". The sidebar checkbox is the only per-calendar visibility control; overrides and "(modified)" persist across restarts.
- R6. The active set applies to Day/Week/Month/Agenda/Tasks, the [tray-flyout](tray-flyout.md), holiday shading ([calendar-shading](calendar-shading.md)) and the default filter for search.
- R7. Sets and sidebar ticks change only what is shown. Every calendar, task list and feed is synced whether shown or not, and reminders fire for every calendar and task list with Reminders on ([local-store-offline](local-store-offline.md) R10, [reminders-notifications](reminders-notifications.md) R1).
- R8. Sets and the active set are stored locally (not synced) and persist across restarts. Removing an account removes its collections from all sets.
  - Storage: tables `calendar_sets` (id, name, position) and `calendar_set_members` (set id, collection id; deleted with the collection) added by a schema migration; the active set id in `meta`. The sidebar ticks stay the current visibility (the `meta` `hidden:` keys): switching a set rewrites them to the set's members, a manual tick changes only them, and the selector shows "(modified)" while they differ from the active set's members.
- R9. Collections added later (a new account, or a calendar created on the server) join only "All" ([account-management](account-management.md) R11).
- R10. A new event or task created in a calendar the active set hides shows the existing "Added to <calendar> (hidden)" banner with Show ([quick-add](quick-add.md) R4); Show ticks it, so the set becomes "(modified)".

## Build slices

Each slice is built, tested and checked in the app before the next starts.

1. Store, sync and selector: R1, R2 (create through Save as new set), R3 (sidebar menu), R4, R5, R6, R7, R8, R9.
2. Settings > Calendar sets (rename, reorder, delete), R10, and removal of the interim notes in other PRDs.

## Out of scope

- Syncing sets between devices.
- Automatic switching (by time of day or Windows Focus).

## Acceptance criteria

- [ ] A "Work" set containing two MS365 calendars and one Radicale calendar shows only those three when selected.
- [ ] Ctrl+2 switches to the second set.
- [ ] Reminders from a calendar outside the active set still fire.
- [ ] An unticked calendar still syncs (its changes on the server appear once it is ticked, without a new sync).
- [ ] Ticking a calendar while "Work" is active shows "Work (modified)"; Update 'Work' saves it, and the mark goes.
- [ ] Ticks, the active set and "(modified)" survive a restart.

## Dependencies

- [calendar-views](calendar-views.md), [account-management](account-management.md), [keyboard-navigation](keyboard-navigation.md), [settings-page](settings-page.md)

## Decisions

- 2026-11-11: hidden calendars are synced and reminded (user's choice); before this, unticked calendars were not pulled and got no reminders.
- 2026-11-11: set members change only from the sidebar (Save as new set, Update); Settings has no member checklist (user's choice).
- 2026-11-11: the sidebar ticks stay stored as the current visibility, so view, search, flyout and picker queries are unchanged; sets rewrite them on switch.
