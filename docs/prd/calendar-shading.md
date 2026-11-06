---
description: Working-hours shading, public-holiday shading from designated holiday calendars, and week numbers
when-to-use: Before working on background shading of time grids/day cells, holiday display, or week numbers
tags: [ui, views, working-hours, holidays, week-numbers]
status: closed
---

# Working hours, public holidays and week numbers

## Summary

The views shade non-working hours and public holidays and can show week numbers. Holidays come from calendars you already sync, such as a holiday calendar subscribed in Radicale (the app fetches its feed, [sync-radicale](sync-radicale.md) R1a). The app does not add subscriptions itself or bundle holiday data.

## Requirements

- R1. Working hours setting: working days (default Mon–Fri) and start/end time (default 09:00–17:00). Local setting only, applied to all accounts.
- R2. Day/Week: time outside working hours, and non-working days, have a subtle shaded background. Month: non-working days have a shaded cell.
- R3. Holiday calendar: the user marks one synced calendar as the "public holiday" calendar from its sidebar right-click menu. At most one calendar has the role at a time: marking another moves it there. Task lists cannot be marked.
- R4. Any all-day event in a holiday calendar marks those dates as holidays:
  - the whole day column (Day/Week) or cell (Month) gets the non-working shading, the same as a weekend;
  - the holiday name appears in the Week and Agenda day headers and beside the date in a Month cell; a long name is trimmed, with the full name in a tooltip. Several holidays on one day are joined with ", ".
  - holiday shading replaces the today and non-working shading on that day; the today chip stays.
- R5. The holiday calendar gets a "Show as shading only" option in the same menu (default on): its all-day events are not drawn as event blocks, only as shading and header labels. Turning it off shows them as normal all-day events too. Its timed events are always drawn normally.
- R6. Holiday shading follows the calendar's visibility and the active calendar set.
- R7. Week numbers: toggle in settings (default off). Shown in the Month view's left column, the Day/Week header ("Wk 45") and the mini month. Numbering follows ISO 8601.
- R8. Shading colours use the tokens in [ui-colour-theme](ui-colour-theme.md) R3: non-working = a light pink (Pink 5 light / Night 100 tinted with Pink 90 dark); holidays use the same non-working shade (the header name tells them apart from a weekend). Primary and secondary text keep WCAG AA contrast on every shading.

## Interim (current build)

- R1 is set on the Settings page, Working hours section ([settings-page](settings-page.md) interim): seven day checkboxes in the Windows week order and Start / End time pickers in 30-minute steps. An End not after Start shows a message and is not saved.
- R2: in Week, a working day's time before Start and after End is shaded with Shade.NonWorking, drawn under the hour lines; days not ticked are shaded all day in Week, Month and Agenda. Month and Agenda do not shade hours. Holidays (R3-R6) are in.
- R7 is the "Show week numbers" toggle in Appearance. There is no Day view or mini month yet, so numbers show in: Month, a narrow column left of each week row; Week, "Wk 45" in the top-left corner above the hour labels, for the week in the middle of the view; Agenda, "Wk 45" in the header label of each week's first day. A week that starts on Sunday shows the ISO number of its Monday.
- The Week day header grows from 52 to 68 epx only when a holiday falls in the loaded range.

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
