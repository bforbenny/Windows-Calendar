---
description: Named calendar sets that switch which calendars and task lists are visible in one click (e.g. Work, Personal)
when-to-use: Before working on calendar visibility, the sidebar, or set switching
tags: [ui, calendars, visibility, sets]
status: closed
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
- R8. Sets and the active set are stored locally (not synced) and persist across restarts. Removing an account removes its collections from all sets, but first remembers, per collection, its set memberships, its sidebar tick and its per-collection settings (colour, shape, colour override, Reminders on; [account-management](account-management.md) R9), keyed by the account's type, sign-in name and server URL (none for MS365) plus the collection's server id (Graph id, or CalDAV URL) and kind. No credentials are stored.
  - Re-adding the same account (same type, sign-in name and server URL) restores these to each collection that matches by server id and kind when it is first discovered, whether when the account is added or by a later sync (MS365 task lists are found by the first sync). Each remembered entry is used once; removing the account again replaces them. Collections that do not match follow R9. A restored member of the active set keeps the selector unmodified.
  - Storage: tables `calendar_sets` (id, name, position) and `calendar_set_members` (set id, collection id; deleted with the collection) added by a schema migration; the active set id in `meta`. The sidebar ticks stay the current visibility (the `meta` `hidden:` keys): switching a set rewrites them to the set's members, a manual tick changes only them, and the selector shows "(modified)" while they differ from the active set's members.
- R9. Collections added later (a new account, or a calendar created on the server) join only "All" ([account-management](account-management.md) R11), except those restored on re-add (R8).
- R10. A new event or task created in a calendar the active set hides shows the existing "Added to <calendar> (hidden)" banner with Show ([quick-add](quick-add.md) R4); Show ticks it, so the set becomes "(modified)".
- R11. Each set also holds the secondary timezone ruler ([timezone-handling](timezone-handling.md) R8): on or off, and its zone.
  - "Save as new set…" saves the current ruler with the ticks, "Update '<set>'" saves both, and switching a set applies its ruler.
  - Changing the ruler while a user-defined set is active marks the set "(modified)", like a tick, until Update or the next switch.
  - "All" holds the default ruler: a change while "All" is active is saved to it at once (never "(modified)"), and a set saved before R11 existed, with no ruler of its own, uses it.
  - The ruler is changed in Settings > Appearance ([settings-page](settings-page.md) R2) or from "Secondary timezone…" in the selector's ⋯ menu; both change the same setting.
  - Storage: two nullable columns on `calendar_sets` (ruler on, zone id) added by a schema migration; the default and the current ruler in `meta`, like the ticks (R8).

- R12. Each set also holds a "Warn about clashes and short gaps" switch ([schedule-gap-warning](schedule-gap-warning.md) R10), set in Settings > Calendar sets next to each set and saved at once. It never marks a set "(modified)". "All" starts on; new user-defined sets start off. Storage: a nullable column on `calendar_sets` (null = the default) and the "All" value in `meta`, added by a schema migration.

## Build slices

Each slice is built, tested and checked in the app before the next starts.

1. Store, sync and selector: R1, R2 (create through Save as new set), R3 (sidebar menu), R4, R5, R6, R7, R8, R9.
2. Settings > Calendar sets (rename, reorder, delete), R10, and removal of the interim notes in other PRDs.
3. Restore on re-add (R8 sub-bullet, R9 exception).
4. Secondary timezone per set: R11, built with [timezone-handling](timezone-handling.md) slice 2 (R8).

## Out of scope

- Syncing sets between devices.
- Automatic switching (by time of day or Windows Focus).

## Acceptance criteria

- [x] A "Work" set containing two MS365 calendars and one Radicale calendar shows only those three when selected.
- [x] Ctrl+2 switches to the second set.
- [x] Reminders from a calendar outside the active set still fire.
- [x] An unticked calendar still syncs (its changes on the server appear once it is ticked, without a new sync).
- [x] Ticking a calendar while "Work" is active shows "Work (modified)"; Update 'Work' saves it, and the mark goes.
- [x] Ticks, the active set and "(modified)" survive a restart.
- [x] Removing an MS365 account and adding it again puts its calendars back in "Work" with their ticks, colours, shapes and Reminders on as before, and "Work" is not "(modified)".
- [x] With a London ruler saved in "Work" and none in "Personal", switching sets shows and hides the ruler; changing the zone while in "Work" shows "Work (modified)" until Update.

## Dependencies

- [calendar-views](calendar-views.md), [account-management](account-management.md), [keyboard-navigation](keyboard-navigation.md), [settings-page](settings-page.md)

## Decisions

- 2026-11-11: hidden calendars are synced and reminded (user's choice); before this, unticked calendars were not pulled and got no reminders.
- 2026-11-11: set members change only from the sidebar (Save as new set, Update); Settings has no member checklist (user's choice).
- 2026-11-11: the sidebar ticks stay stored as the current visibility, so view, search, flyout and picker queries are unchanged; sets rewrite them on switch.
- 2026-11-11: re-adding a removed account restores its collections' set memberships, ticks, colour, shape and Reminders on (user's choice); found when an MS365 account was removed and re-added while troubleshooting and its calendars dropped out of the active set.
- 2026-11-11: each set holds the secondary timezone ruler (on/off and zone), saved like the ticks (Save as new set, Update, "(modified)"); "All" holds the default, saved at once. The display timezone stays the Windows timezone (user's choice).
