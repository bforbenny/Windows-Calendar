---
description: Typography, spacing, corner radius, hit targets, motion and locale formats for the whole UI
when-to-use: Before styling any screen, control, text, animation, or date/time display
tags: [ui, typography, spacing, motion, locale]
status: pending
---

# UI style

## Summary

The app follows Windows 11 Fluent conventions so it looks native and stays sharp at 100% and 150% scaling. Brand identity comes from the colour palette ([ui-colour-theme](ui-colour-theme.md)), not from a custom font or custom motion.

## Requirements

- R1. Font: Segoe UI Variable (Windows system font) everywhere. No bundled fonts. Icons use Segoe Fluent Icons.
- R2. Type ramp (size / line height in epx), using the WinUI built-in text styles:

  | Style | Size / line | Use |
  | --- | --- | --- |
  | Caption | 12 / 16 | short-event labels, secondary metadata, week numbers |
  | Body | 14 / 20 | default text |
  | Body Strong | 14 / 20 semibold | event titles, day headers |
  | Subtitle | 20 / 28 | section headings, dialog titles |
  | Title | 28 / 36 | current period title (e.g. "November 2026") |

  No text smaller than 12 epx.
- R3. Spacing on a 4 epx grid; standard steps 4, 8, 12, 16, 24 epx.
- R4. Corner radius: 4 epx for controls and event cards, 8 epx for dialogs, flyouts and the tray flyout (WinUI defaults).
- R5. Interactive targets are at least 32×32 epx (mouse and trackpad use).
- R6. Motion: WinUI built-in transitions for view switches, flyouts, dialogs and the undo banner. Drag-move and resize follow the pointer directly with no easing. When Windows "Animation effects" is off (`UISettings.AnimationsEnabled` false), all non-essential animation is disabled.
- R7. Time and date formats come from the Windows region settings (12/24-hour clock, short and long date patterns, first day of week default). There is no separate format setting in the app.

## Out of scope

- Brand or custom fonts.
- Custom animation curves or decorative motion.
- A density/compact mode.

## Acceptance criteria

- [ ] All text uses WinUI text styles from R2; no hard-coded font sizes outside the style definitions.
- [ ] Switching Windows to 24-hour time changes times in all views after restart (or live, if Windows notifies).
- [ ] With Windows "Animation effects" off, view switches and flyouts appear without animation.

## Dependencies

- [app-platform](app-platform.md), [ui-colour-theme](ui-colour-theme.md), [display-scaling](display-scaling.md)
