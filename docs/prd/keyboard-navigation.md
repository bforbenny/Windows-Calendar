---
description: Full keyboard operation of the app, including moving/resizing events without the mouse, and a shortcut cheat sheet
when-to-use: Before adding any UI, shortcut, or focus behaviour
tags: [ui, keyboard, accessibility, shortcuts]
status: pending
---

# Keyboard navigation

## Summary

Every action in the app can be done from the keyboard, including the drag operations, with a consistent shortcut set and a cheat sheet. This replaces the short list in [calendar-views](calendar-views.md) R11.

## Requirements

- R1. All controls are reachable by Tab, with a visible focus indicator and access keys (Alt) on toolbar commands.
- R2. Shortcuts (main window):

  | Key | Action |
  | --- | --- |
  | W / M / A / K | Week / Month / Agenda / Tasks view |
  | T | Go to today |
  | PgUp / PgDn | Scroll back / forward one screen ([calendar-views](calendar-views.md) R1a) |
  | G | Go to date (date picker / typed date) |
  | Arrows | Move selection between events (Up/Down within a day, Left/Right across days) |
  | Enter | Open selected item in the editor panel |
  | Esc | Close editor panel / popup / clear selection |
  | Ctrl+N | Quick add ([quick-add](quick-add.md)) |
  | Ctrl+D | Duplicate selected ([duplicate-event](duplicate-event.md)) |
  | Del | Delete selected (undo banner, see [undo](undo.md)) |
  | Ctrl+Z | Undo |
  | Ctrl+F / Ctrl+E | Search |
  | Ctrl+J / Ctrl+Shift+J | Join selected / next meeting ([online-meeting-join](online-meeting-join.md)) |
  | Ctrl+1 … Ctrl+9 | Switch calendar set ([calendar-sets](calendar-sets.md)) |
  | F5 | Sync now |
  | Ctrl+, | Settings |
  | ? (Shift+/) | Show shortcut cheat sheet |

- R3. Keyboard move/resize of the selected event (same rules as drag, including the recurring scope prompt):
  - Alt+Up / Alt+Down: move by 15 minutes; Alt+Left / Alt+Right: move by one day.
  - Alt+Shift+Up / Alt+Shift+Down: shorten / lengthen by 15 minutes.
- R4. Single-letter shortcuts are disabled while focus is in a text field.
- R5. Shortcuts are fixed in v1 (not user-remappable) and listed in the cheat sheet.
- R6. Screen-reader names are provided for event blocks (title, time, calendar, show-as).

## Out of scope

- User-customisable shortcuts.
- Global (system-wide) hotkeys.

## Acceptance criteria

- [ ] Create, open, move, resize, duplicate, delete and undo an event without touching the mouse.
- [ ] Every shortcut in R2 works and appears in the cheat sheet.
- [ ] Typing "w" in the title field does not switch to Week view.

## Dependencies

- [calendar-views](calendar-views.md), [agenda-view](agenda-view.md), [undo](undo.md), [quick-add](quick-add.md), [calendar-sets](calendar-sets.md)
