---
description: Full keyboard operation of the app, including moving/resizing events without the mouse, and a shortcut cheat sheet
when-to-use: Before adding any UI, shortcut, or focus behaviour
tags: [ui, keyboard, accessibility, shortcuts]
status: in-progress
---

# Keyboard navigation

## Summary

Every action in the app can be done from the keyboard, including the drag operations, with a consistent shortcut set and a cheat sheet. This replaces the short list in [calendar-views](calendar-views.md) R11.

## Requirements

- R1. All controls are reachable by Tab, with a visible focus indicator and access keys (Alt) on toolbar commands.
- R2. Shortcuts (main window):

  | Key | Action |
  | --- | --- |
  | F1 / F2 / F3 / F4 | Week / Month / Agenda / Tasks view |
  | T | Go to today |
  | PgUp / PgDn | Scroll back / forward one screen ([calendar-views](calendar-views.md) R1a) |
  | Ctrl+G | Go to date (calendar flyout) |
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
  | Ctrl+B | Hide / show the sidebar ([calendar-views](calendar-views.md) R2b) |
  | ? (Shift+/) | Show shortcut cheat sheet |

- R3. Keyboard move/resize of the selected event (same rules as drag, including the recurring scope prompt):
  - Alt+Up / Alt+Down: move by 15 minutes; Alt+Left / Alt+Right: move by one day.
  - Alt+Shift+Up / Alt+Shift+Down: shorten / lengthen by 15 minutes.
- R4. Single-letter shortcuts are disabled while focus is in a text field.
- R5. Shortcuts are fixed in v1 (not user-remappable) and listed in the cheat sheet.
- R6. Screen-reader names are provided for event blocks (title, time, calendar, show-as).

## Interim (current build)

- Built: F1-F4, T, PgUp / PgDn, Ctrl+G, Arrows, Enter, Esc, Del, Ctrl+D, Ctrl+J / Ctrl+Shift+J (branch `online-meeting-join`), Ctrl+Z, F5, Ctrl+, , Ctrl+B, ? and R3. Ctrl+N presses the toolbar New button (New event, or New task in the Tasks view) until quick add takes the key. Ctrl+F / Ctrl+E and Ctrl+1-9 come with their features.
- Selection (Week, Month, Agenda): the first arrow press selects the first item on today, else the first one from the day in view. Up/Down move within the day (all-day items first, then by start); Left/Right move to the item nearest in time on the previous / next day that has one. The selected card has an accent outline and is scrolled into view. Clicking a card selects it as well as opening it. Esc clears the selection when no panel is open. Enter opens the selected event or task.
- Del deletes the selected event through the editor's Delete (its confirmation and repeating-event prompt; undo for deletes comes with [undo](undo.md)). Tasks are not deleted from the calendar.
- R3 goes through the drag path: the same recurring scope prompt, Undo banner and rules; read-only events, invitations and tasks do not move. All-day events move only by day; resize needs a timed event and keeps at least 15 minutes.
- R4: T, ?, arrows, Enter and Del are ignored while focus is in a text field, drop-down, the editor panel or Settings. Arrows select events even when focus is on the view tabs (F1-F4 switch views).
- Holding Ctrl for about half a second shows a small badge with the key under each control that has one: F1-F4 on the view tabs, T on Today, Ctrl+G on the period title, Ctrl+N on New, F5 on Sync, Ctrl+, on Settings, Ctrl+B on the sidebar button, and Ctrl+Z on Undo while the undo banner shows. The badges hide when Ctrl is released, another key is pressed or the window loses focus.
- ? shows the cheat sheet: a dialog listing the built shortcuts.

## Out of scope

- User-customisable shortcuts.
- Global (system-wide) hotkeys.

## Acceptance criteria

- [ ] Create, open, move, resize, duplicate, delete and undo an event without touching the mouse.
- [ ] Every shortcut in R2 works and appears in the cheat sheet.
- [ ] Typing "w" in the title field does not switch to Week view.

## Dependencies

- [calendar-views](calendar-views.md), [agenda-view](agenda-view.md), [undo](undo.md), [quick-add](quick-add.md), [calendar-sets](calendar-sets.md)
