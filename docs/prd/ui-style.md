---
description: Typography, spacing, corner radius, hit targets, motion and locale formats for the whole UI
when-to-use: Before styling any screen, control, text, animation, or date/time display
tags: [ui, typography, spacing, motion, locale]
status: closed
---

# UI style

## Summary

The app follows Windows 11 Fluent conventions so it looks native and stays sharp at 100% and 150% scaling. Brand identity comes from the colour palette ([ui-colour-theme](ui-colour-theme.md)), not from a custom font or custom motion.

## Requirements

- R1. Font: Segoe UI Variable (Windows system font) everywhere. No bundled fonts. Icons use Segoe Fluent Icons.
- R2. Type ramp (size / line height in epx), defined once as app text styles (WPF-UI's Fluent ramp):

  | Style | Size / line | Use |
  | --- | --- | --- |
  | Caption | 12 / 16 | short-event labels, secondary metadata, week numbers |
  | Body | 14 / 20 | default text |
  | Body Strong | 14 / 20 semibold | event titles, day headers |
  | Subtitle | 20 / 28 | section headings, dialog titles, the current period title (e.g. "November 2026") |
  | Title | 28 / 36 | not used in the main window; the toolbar has no room for it at 1000–1500 epx (decided 2026-11-12) |

  No text smaller than 12 epx.
- R3. Spacing on a 4 epx grid; standard steps 4, 8, 12, 16, 24 epx.
- R4. Corner radius: 4 epx for controls and event cards, 8 epx for dialogs, flyouts and the tray flyout (Fluent defaults).
- R5. Interactive targets are at least 32×32 epx (mouse and trackpad use).
- R6. Motion: short built-in or WPF-UI transitions for view switches, flyouts, dialogs and the undo banner. Drag-move and resize follow the pointer directly with no easing. When Windows "Animation effects" is off (`UISettings.AnimationsEnabled` false), all non-essential animation is disabled.
- R7. Time and date formats come from the Windows region settings (12/24-hour clock, short and long date patterns, first day of week default). There is no separate format setting in the app.

## Build details

- R1: the app font is "Segoe UI Variable Text", falling back to Segoe UI, set on each window (the controls inherit it) and on the menu and tooltip styles.
- R5: dense controls (sidebar checkboxes and chevrons, Month's "+n more", card checkboxes, the Join pill) keep their look; their clickable area is enlarged to 32×32 epx with padding or a transparent margin. Exception: "+n more" is clickable across the whole cell width at the chip-row height, since a taller area would cover the chip above or the next week.
- R7: fixed date patterns (e.g. "MMM d", "ddd d MMM") take their day and month order from the Windows short-date pattern (day first when it puts d before M, as quick add does), since Windows lets the user change that pattern but not the month-day one. A Windows region change is applied live: the cached culture data is cleared and the views redraw.

## Decisions

- 2026-11-12: the period title uses Subtitle, not Title (user's choice).
- 2026-11-12: small controls get a larger hit area rather than a larger look (user's choice).
- 2026-11-12: the pending-changes badge on Sync uses Caption (12 epx), not 10 epx, so no text is below 12 epx (user's choice).
- 2026-11-12: the date order comes from the short-date pattern, not the month-day pattern, so a customised short date (e.g. dd MMM yyyy on en-US) is followed (user's choice).
- 2026-11-12: "+n more" keeps the chip-row height and is clickable across the cell instead of 32 epx tall (user's choice).

## Out of scope

- Brand or custom fonts.
- Custom animation curves or decorative motion.
- A density/compact mode.

## Acceptance criteria

- [x] All text uses the app text styles from R2; no hard-coded font sizes outside the style definitions.
- [x] Switching Windows to 24-hour time changes times in all views after restart (or live, if Windows notifies).
- [x] With Windows "Animation effects" off, view switches and flyouts appear without animation.

## Dependencies

- [app-platform](app-platform.md), [ui-colour-theme](ui-colour-theme.md), [display-scaling](display-scaling.md)
