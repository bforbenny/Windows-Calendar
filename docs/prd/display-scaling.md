---
description: Layout rules for display scaling and window sizes, targeting 1920x1080 at 100% and the Framework 13 laptop at 150%
when-to-use: Before working on window layout, sizing, responsive behaviour, multi-monitor moves, or DPI-related rendering
tags: [ui, layout, dpi, scaling, multi-monitor]
status: pending
---

# Display scaling and layout

## Summary

The app is used on two displays: an external 1920×1080 monitor at 100% scaling (docked) and a Framework 13 laptop panel at 150% scaling (undocked). Layout is designed in effective pixels (epx), so it must work fully at the smaller effective size and move cleanly between the two.

## Requirements

- R1. Design targets, in effective pixels:

  | Setup | Physical | Scale | Effective |
  | --- | --- | --- | --- |
  | External monitor | 1920×1080 | 100% | 1920×1080 |
  | Framework 13 (2256×1504 panel) | 2256×1504 | 150% | 1504×1003 |
  | Framework 13 (2880×1920 panel) | 2880×1920 | 150% | 1920×1280 |

  The smallest target (1504×1003 epx) is the reference: every view must be fully usable when the window is maximised there.
- R2. Per-monitor DPI awareness (WinUI 3 default). Moving the window between displays re-renders at the new scale with no blur and no layout break.
- R3. Window size and position:
  - minimum window size 1000×640 epx;
  - the size, position and maximised state are restored on start;
  - if the saved position is not on any connected display (e.g. after undocking), the window opens centred on the primary display at a size that fits;
  - while running, if a display is removed (e.g. undocking), Windows moves the window; if it then extends past the work area, it is resized to fit.
- R4. Responsive sidebar: at window width ≥ 1200 epx the sidebar is expanded (about 260 epx); below 1200 epx it collapses to an icon rail, and can be opened as an overlay. Crossing 1200 epx with the editor open switches it between push and overlay ([calendar-views](calendar-views.md) R6c) without losing focus or an unsaved field value.
- R5. Day/Week time grid:
  - hour height 64 epx (shows about 7–8 hours at 1504×1003 maximised); with the 24 epx text threshold ([calendar-views](calendar-views.md) R4a), events of 22.5 minutes or longer show text and 15-minute events show as a bar only;
  - on open, the grid scrolls so that the start of working hours ([calendar-shading](calendar-shading.md) R1) is at the top.
- R6. Month view: each cell shows as many event chips as fit, then "+n more". At 1504×1003 at least 3 chips per cell are visible.
- R7. Crisp rendering at 150%: layout rounding stays on (WinUI default), dividers are 1 epx, icons come from the Segoe Fluent Icons font, and shape markers ([ui-colour-theme](ui-colour-theme.md) R7) are vector-drawn. No bitmap icons in the UI.
- R8. Text uses the Fluent type ramp (Body 14 epx, Caption 12 epx; nothing smaller than 12 epx). Windows "Text size" (Accessibility) is honoured: text grows without clipping; event cards truncate with an ellipsis and show the full title in a tooltip.
- R9. The tray flyout ([tray-flyout](tray-flyout.md)) opens on the display that holds the taskbar being clicked, at that display's scale.

## Out of scope

- Displays below 1000×640 epx, tablet/touch-first layouts and portrait mode.
- A separate compact or density setting.

## Acceptance criteria

- [ ] At 1504×1003 epx (Framework 13 at 150%), Day, Week, Month and Agenda views and the event editor are fully usable with no clipped controls.
- [ ] Dragging the window from the 100% monitor to the 150% panel re-renders sharply with no layout break.
- [ ] Undocking with the window last placed on the external monitor opens the window on the laptop panel.
- [ ] At Windows Text size 150%, no text in the main views is clipped.
- [ ] Below 1200 epx width the sidebar collapses to the icon rail.

## Dependencies

- [app-platform](app-platform.md), [calendar-views](calendar-views.md), [calendar-shading](calendar-shading.md), [tray-flyout](tray-flyout.md), [ui-colour-theme](ui-colour-theme.md)
