---
description: Working-hours shading, public-holiday shading from designated holiday calendars, and week numbers
when-to-use: Before working on background shading of time grids/day cells, holiday display, or week numbers
tags: [ui, views, working-hours, holidays, week-numbers]
status: in-progress
---

# Working hours, public holidays and week numbers

## Summary

The views shade non-working hours and public holidays and can show week numbers. Holidays come from calendars you already sync, such as a holiday calendar subscribed in Radicale (the app fetches its feed, [sync-radicale](sync-radicale.md) R1a). The app does not add subscriptions itself or bundle holiday data.

## Requirements

- R1. Working hours setting: working days (default Mon–Fri) and start/end time (default 09:00–17:00). Local setting only, applied to all accounts.
- R2. Day/Week: time outside working hours, and non-working days, have a subtle shaded background. Month: non-working days have a shaded cell.
- R3. Holiday calendars: in settings, the user marks one or more synced calendars as "public holiday" calendars.
- R4. Any all-day event in a holiday calendar marks those dates as holidays:
  - the whole day column (Day/Week) or cell (Month) gets a distinct holiday shading (stronger than non-working shading);
  - the holiday name appears in the day header and in the Agenda day header.
- R5. Holiday calendars get a "Show as shading only" option (default on): their events are not drawn as event blocks, only as shading and header labels. Turning it off shows them as normal all-day events too.
- R6. Holiday shading follows the calendar's visibility and the active calendar set.
- R7. Week numbers: toggle in settings (default off). Shown in the Month view's left column, the Day/Week header ("Wk 45") and the mini month. Numbering follows ISO 8601.
- R8. Shading colours use the tokens in [ui-colour-theme](ui-colour-theme.md) R3: non-working = a light pink (Pink 5 light / Night 100 tinted with Pink 90 dark); holiday = Amber 10 / Amber 90, so holidays differ from non-working time by hue as well as lightness. Primary and secondary text keep WCAG AA contrast on every shading.

## Interim (current build)

- Working hours are not configurable yet: Saturday and Sunday are the non-working days, shaded with Shade.NonWorking over the whole day in Week, Month and Agenda. Non-working hours on weekdays, holidays (R3-R6) and week numbers (R7) come later.

## Out of scope

- Subscribing to ICS URLs or bundling holiday data (holidays come via Radicale).
- Per-day different working hours, per-account working hours, reading Outlook's working hours from Graph.
- Non-ISO week numbering schemes.

## Acceptance criteria

- [ ] With 09:00–17:00 Mon–Fri, evenings and weekends are shaded in Week view.
- [ ] Marking a Radicale holiday calendar shades 25 Dec and shows "Christmas Day" in the header, without an event block.
- [ ] Week numbers match ISO 8601 (e.g. 5 Nov 2026 is week 45).

## Dependencies

- [calendar-views](calendar-views.md), [agenda-view](agenda-view.md), [calendar-sets](calendar-sets.md), [sync-radicale](sync-radicale.md), [ui-colour-theme](ui-colour-theme.md)
