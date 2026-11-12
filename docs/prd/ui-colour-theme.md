---
description: Colour palette, colour roles (tokens) for light and dark themes, calendar colours and calendar shape markers
when-to-use: Before choosing any colour in the UI, styling event cards, status indicators, buttons, shading, or calendar swatches
tags: [ui, theme, colour, accessibility, dark-mode]
status: closed
---

# UI colour theme

## Summary

All UI colours come from one palette of 10-step ramps, where a given step has the same lightness in every hue. Colours are referenced by role (token), never by hex value, so light, dark and high-contrast themes swap cleanly. Calendars get one of 24 colours plus a shape marker, so calendars can be told apart without relying on hue (colour-blind safe).

## Requirements

- R1. Palette (hex, steps 10–100). Brand ramps:

  | Ramp | 10 | 20 | 30 | 40 | 50 | 60 | 70 | 80 | 90 | 100 |
  | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
  | Navy | E8EFFD | C2D5FA | 97B7F7 | 6E9BF4 | 437CF1 | 155CEE | 0E49BF | 0B3892 | 08296C | 041639 |
  | Sea Green | D1F7EB | 7BE8C7 | 25CF9C | 1FB085 | 1A936F | 157659 | 115E47 | 0D4837 | 093528 | 051B15 |
  | Strawberry Red | FCEAEA | F7CACA | F1A1A1 | EC7A7A | E54B4B | CD1E1E | A31717 | 801212 | 5E0E0E | 340808 |
  | Amber | FFEEBF | FFCF4E | EAAA00 | C99200 | A77900 | 886300 | 6B4E00 | 523C00 | 3C2B00 | 1F1700 |
  | Grey (light UI) | EEEEF0 | D2D4D9 | B3B7BE | 969CA6 | 7B828F | 636874 | 4E535C | 3C4046 | 2B2D33 | 16181A |
  | Night (dark UI) | EBEFF5 | CCD4E6 | A8B7D5 | 879CC5 | 6781B6 | 4D689F | 3C527D | 2E3F61 | 222D46 | 121824 |

  Extended ramps for calendar colours (draft; generated to match the brand lightness steps):

  | Ramp | 10 | 20 | 30 | 40 | 50 | 60 | 70 | 80 | 90 | 100 |
  | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
  | Purple | F0EDF6 | DBCFF4 | C6A9FA | B484FF | A259FF | 9026F5 | 6F2BB9 | 512984 | 39225A | 1D122D |
  | Teal | D0F6F5 | 6DE7E6 | 2ECACA | 08AEAE | 069090 | 187474 | 205B5B | 204545 | 1C3231 | 0F1A19 |
  | Orange | F6EDE8 | F5CCB6 | FBA16E | FA7603 | D06101 | A65114 | 7F431D | 5D351D | 412819 | 22150E |
  | Pink | F6ECF0 | F5C8DC | FB98C8 | FF61B7 | F402A2 | C31B82 | 942665 | 6C244B | 4B1E36 | 27111C |

  Any adjustment to an extended ramp must keep each step's contrast against white within ±0.1 of the brand ramps' value for that step.

- R2. Contrast rules (from the palette's equal-lightness steps):
  - Text on a light surface (white, step 10–20) uses step 60 or darker. Text on a dark surface (Night 90–100) uses step 40 or lighter.
  - Step 50 is never used for text; it is for bars, icons and large shapes only (≥ 3:1).
  - Light surfaces in the light theme are white (FFFFFF) and Grey; dark surfaces in the dark theme are Night.
- R3. Colour roles. Code references these tokens only; values are defined once in theme resource dictionaries (Light, Dark, HighContrast), swapped when the theme changes.

  | Token | Light | Dark |
  | --- | --- | --- |
  | Surface.Base (grid, cards, flyouts) | FFFFFF | Night 100 |
  | Surface.Raised (dialogs, popups) | FFFFFF + shadow | Night 90 |
  | Shade.NonWorking | FBF6F8 (Pink 5: halfway between white and Pink 10) | 201928 (Night 100 with a quarter of Pink 90) — a light pink, lighter than Grey 10 |
  | Shade.Holiday | same as Shade.NonWorking | same as Shade.NonWorking |
  | Shade.Today (today's column/cell) | F4F7FE (Navy 5: halfway between white and Navy 10) | 0F1E3C (between Night 100 and Navy 90) — a faint blue, softer than Navy 10 / 90 and distinct from Shade.NonWorking |
  | Divider | Grey 20 | Night 80 |
  | Text.Primary | Grey 100 | Night 10 |
  | Text.Secondary | Grey 70 | Night 30 |
  | Text.Disabled | Grey 50 | Night 60 |
  | Accent (primary/create buttons, selection, links) | Navy 60 | Navy 40 |
  | Accent.Hover / Accent.Pressed | Navy 70 / Navy 80 | Navy 30 / Navy 50 |
  | Text.OnAccent | FFFFFF | Night 100 |
  | Destructive (delete, discard) | Red 60 | Red 40 |
  | Destructive.Hover / Destructive.Pressed | Red 70 / Red 80 | Red 30 / Red 50 |
  | Text.OnDestructive | FFFFFF | Night 100 |
  | Today.DateChip | Navy 60, white text | Navy 40, Night 100 text |
  | NowLine (current-time line and dot) | Red 60 | Red 40 |
  | Status.Error / Conflict | Red 60 | Red 40 |
  | Status.Warning / Offline | Amber 60 | Amber 40 |
  | Status.Success / Synced | Sea Green 60 | Sea Green 40 |
  | Status.Info / Syncing | Navy 60 | Navy 40 |
  | Selection outline (selected event) | Grey 100, 2 epx | Night 10, 2 epx |

- R4. Status colours are always paired with an icon and/or text label; no state is conveyed by colour alone. Calendars may use Red, Amber and Sea Green hues.
- R5. The app overrides the Windows accent colour with the Accent token inside the app. Focus rectangles use the WPF-UI focus visual (unchanged colours).
- R6. Calendar colours: 24 = 8 hues × 3 levels.
  - Hues: Navy, Sea Green, Strawberry Red, Amber, Purple, Teal, Orange, Pink.
  - Each calendar colour is (hue, level). Per-theme steps:

    | Part | Light L1 / L2 / L3 | Dark L1 / L2 / L3 |
    | --- | --- | --- |
    | Swatch, shape marker, event left bar, Free outline | 50 / 70 / 90 | 20 / 40 / 50 |
    | Event fill (Busy) | 10 / 20 / 30 | 90 / 80 / 70 |
    | Event text | Grey 100 | Night 10 |

  - All combinations meet WCAG AA for text (≥ 4.5:1) and 3:1 for swatches/markers against the surfaces they sit on: Surface.Base, Surface.Raised, Shade.Today and Shade.NonWorking.
  - Dark level 3 was step 60 until 2026-11-12; it fell to 2.5:1 on Surface.Raised and 3.0:1 on Shade.Today, so it moved to step 50 (at least 3.5:1). Dark levels 2 and 3 are closer in lightness as a result.
- R7. Calendar markers: a shape (up triangle, down triangle, square, circle, pentagon) or a Fluent icon from a fixed set (Segoe Fluent Icons), drawn in the swatch colour.
  - On by default; a setting "Show calendar shapes" turns them off.
  - The marker is drawn filled in the calendar's swatch colour: as the sidebar swatch, before the title on event cards (Day/Week/Month), in Agenda rows, tray flyout rows, search results and the quick-add preview. Size 10 epx, vector-drawn.
  - The user can change a calendar's marker to another shape or an icon; the choice is stored locally, never sent to the server.
- R8. Automatic assignment for a new calendar (when no override exists): calendar number i (0-based, in order added) gets hue `[Navy, Orange, Teal, Pink, Sea Green, Purple, Amber, Strawberry Red][i mod 8]`, level `L((i mod 3) + 1)`, and shape `[circle, square, up triangle, down triangle, pentagon][i mod 5]`, skipping combinations already in use. Adjacent calendars therefore differ in hue, lightness and shape.
- R9. Server colours (Graph `hexColor`/`color`, CalDAV `calendar-color`) are snapped to the nearest of the 24 calendar colours (smallest CIELAB ΔE). Overrides and snapping are local only; nothing is written back to the server.
- R10. Past events are drawn the same as future events; the now line and today shading show where the present is. (Dimming past events was rejected: a lighter bar falls below 3:1 for level-1 colours.)
- R11. High contrast: when a Windows high-contrast theme is active, the palette is not used. All tokens map to system high-contrast colours, and calendars are distinguished by name and shape marker only.

## Interim (server colours)

- R9: Radicale `calendar-color` (`#RRGGBB` or `#RRGGBBAA`, alpha ignored) and Graph `hexColor` are read on every re-discovery ([account-management](account-management.md) R11). The Graph `color` preset is not used: with no `hexColor`, the colour stays automatic. To Do lists have no server colour.
- Snapping compares the server colour with each calendar colour's light-theme swatch step (50 / 70 / 90) by CIELAB ΔE (CIE76).
- A collection with a server colour takes the snapped colour on every sync, existing collections included, so a colour change on the server is followed. A collection without one keeps its automatic colour (R8). Two calendars may snap to the same colour; their shapes stay automatic. A CalDAV collection holding events and tasks gives both its calendar and its task list the same colour.
- Overrides: right-clicking a calendar or task list in the sidebar offers **Colour** (the 24 colours as a 3 x 8 grid, the current one marked), **Icon** (the 5 shapes, then about 40 Fluent icons) and, when the colour was overridden and the server has a colour, **Use server colour**. The settings page takes these over later ([settings-page](settings-page.md)).
- A chosen colour is an override: later server colours no longer change it, until **Use server colour** returns to the (snapped) server colour. Icons and shapes never come from the server.
- Icons are drawn at the marker size, in the swatch colour (system colours in high contrast), wherever shapes are drawn.
- Markers on event cards sit on the card fill, which R6 does not check: dark level 3 (swatch 50 on fill 70) is only 2.0:1. On cards, a dark level 3 marker (shape, icon or task checkbox) uses step 20 of its hue instead (at least 5.2:1 on that fill). All other combinations are at least 3.3:1 and keep the swatch step; markers elsewhere sit on the surface and are unchanged.

## Interim (contrast test)

- The automated contrast test (`ThemeContrastTests` in WindowsCalendar.Core.Tests) checks the values in `WindowsCalendar.Core/Views/ThemeColours.cs`, which the app's Theme and CalendarPalette read.
- Text pairs (≥ 4.5:1): Text.Primary, Text.Secondary and Accent (as links) on each surface; Text.OnAccent on Accent and its hover/pressed steps; Text.OnDestructive on Destructive and its steps; Today.DateChip text on the chip; event text on each calendar fill.
- Non-text (≥ 3:1): NowLine, Status colours and the selection outline on each surface; every calendar swatch on each surface.
- Exempt, as WCAG allows: Text.Disabled and Divider.
- Accent hover/pressed, Text.Disabled and the Status colours are drawn by WPF-UI today (its accent manager and SystemFillColor brushes), not by the app's tokens. The test checks their R3 palette values; switching the app to them is not done yet.
- The hex scan fails on any `#RRGGBB(AA)` or quoted 6/8-digit hex in the app's .cs and .xaml files outside ThemeColours.cs and CalendarColours.cs.

## Out of scope

- User-defined custom colours (hex picker) or custom themes.
- Writing calendar colours or shapes back to MS365 or Radicale.
- Per-calendar choice of light/dark fill styles.

## Acceptance criteria

- [x] An automated test computes WCAG contrast for every token pair in R3 and every calendar colour combination in R6, in both themes, and all pass (text ≥ 4.5:1, swatches/markers ≥ 3:1).
- [x] No hex colour literal appears in XAML or C# outside the theme dictionaries.
- [x] With a Windows colour-blindness filter (Settings > Accessibility > Color filters, red-green and blue-yellow), any 8 automatically assigned calendars can be told apart in Week view.
- [x] Turning on a Windows high-contrast theme removes all palette colours and the app stays fully usable.
- [x] Delete buttons are Red; create/save buttons are Navy.

## Dependencies

- [app-platform](app-platform.md), [calendar-views](calendar-views.md), [event-show-as](event-show-as.md), [calendar-shading](calendar-shading.md)
