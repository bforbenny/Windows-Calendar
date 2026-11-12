---
description: Correct timezone and DST handling across Graph and CalDAV, per-event timezone selection, and a secondary timezone column
when-to-use: Before working on date/time storage, display, conversion, or any timezone UI
tags: [timezone, dst, datetime, ui]
status: closed
---

# Timezone handling

## Summary

Times display correctly across DST and backends. Graph uses Windows timezone IDs, while CalDAV uses IANA IDs and VTIMEZONE. Users can set the timezone for each event and can show a secondary timezone column in the Day/Week views.

## Requirements

- R1. Internal representation: store instants in UTC, plus the event's own timezone ID as the backend sent it (a Windows ID from Graph, the TZID from CalDAV) and the floating/all-day flag. IDs are converted only where needed: moving between backends, the editor picker and display. No migration. All-day events are date-only and are never timezone-shifted.
- R2. Conversion: use .NET `TimeZoneInfo` with IANA ↔ Windows ID conversion (`TimeZoneInfo.TryConvertIanaIdToWindowsId` / reverse, ICU on Windows 11). Graph writes use the Windows ID; CalDAV writes use the IANA TZID with a matching VTIMEZONE.
- R3. Custom/non-standard VTIMEZONE definitions from the server are honoured for expansion (Ical.Net), and the original definition is preserved on round-trip.
- R4. Recurrence expansion happens in the event's own timezone (not UTC), so DST transitions keep the wall-clock time.
- R5. Floating times (CalDAV, no TZID) are interpreted in the display timezone and are kept floating on write.
- R6. Display timezone = Windows system timezone. The app reacts to system timezone changes without a restart, through the shared system-change hook ([reminders-notifications](reminders-notifications.md) R2): it regenerates the occurrence index ([local-store-offline](local-store-offline.md) R3), reschedules reminders and refreshes the views.
  - The reaction is local and immediate: it does not wait behind a running network sync. It also refreshes the tray flyout and an open editor's times, and the stored start/end of Graph all-day events (kept as midnight in the zone at sync time).
- R7. **Per-event timezone:** the event editor has a timezone picker (default: system timezone), searchable by city/name (the Windows timezone display names, e.g. "(UTC+10:00) Canberra, Melbourne, Sydney"; no new package). Hidden for all-day events.
  - Separate end timezone: a "Separate end timezone" toggle shows a second picker. It maps to Graph `end.timeZone` and CalDAV `DTEND;TZID`. An event from sync whose end zone differs opens with the toggle on.
  - A floating CalDAV event shows "Floating (no timezone)" and a Graph custom zone (e.g. `tzone://Microsoft/Custom`) shows "Custom (kept)" as an extra entry; either is kept unless the user picks another zone.
  - Allowed at any edit scope ([recurring-events](recurring-events.md)): whole series (exception dates, `UNTIL` and Graph `recurrenceTimeZone` converted; changed occurrences that cannot be kept are listed as lost, as a repeat-rule change does), this occurrence (the occurrence gets its own zone: a CalDAV override with its own `DTSTART;TZID`, a Graph instance PATCH with its own `start.timeZone`), this and following (the series splits and the new series uses the new zone).
  - `EventFields` carries the start and end zones through the CalDAV, Graph, recurrence and transfer code, which today all use the Windows timezone.
- R8. **Secondary timezone column:** an optional second time ruler in the Day/Week views showing a user-chosen timezone, with a label (e.g. "UTC", "London"). Toggle and timezone are set in Settings > Appearance ([settings-page](settings-page.md) R2) or the calendar set's ⋯ menu, and are saved per calendar set, with "All" holding the default ([calendar-sets](calendar-sets.md) R11).
  - One ruler serves all visible days, so its labels use today's offset when today is on screen, else the first visible day's. When the offset between the two zones changes within the visible days (a DST change in either zone), a short note under the ruler label says from which day it differs (e.g. "+1 h from Sun").
- R9. Events whose timezone differs from the display timezone show their original-timezone time in the event details.
  - In the event editor, the time pickers show the event's own zone (next to the R7 picker) and a caption gives the display-timezone time (e.g. "14:00 – 15:00 London time"); card tooltips and screen-reader names give the own-zone time.

## Current state (audited 2026-11-11)

Built: R2 reads and transfer (Graph read in UTC, Graph edits write the event's own zone, CalDAV create writes the IANA TZID with a VTIMEZONE, transfer converts IANA ↔ Windows), R4 (Ical.Net expands in the event's zone; Graph series are expanded by the server), R5 (floating read in the Windows zone and kept floating on write), most of R6 (`SystemEvents.TimeChanged` asks for a sync run, which clears the zone cache, rebuilds the occurrence index, reschedules reminders and refreshes the views). Not built: R7, R8, R9.

Fixes needed in built code (slice 1):

- F1. A Graph event in a custom or unknown zone (e.g. `tzone://Microsoft/Custom`) is rewritten into the system zone on a time edit; a CalDAV event whose TZID Windows does not know is rewritten to UTC. Both must keep their zone (R3).
- F2. A separate end zone (Graph `end.timeZone` / `originalEndTimeZone`, CalDAV `DTEND;TZID`) collapses into the start zone on a time edit, in both backends.
- F3. Graph all-day events store `start_utc` as midnight in the zone at sync time and are not recomputed on a zone change, so code that uses the item's start as an occurrence id or day (series edits, duplicate) can pick the wrong date.
- F4. Moving a series measures the wall-clock shift in the Windows zone and applies it in the event's zone; wrong when the two zones change DST on different dates. CalDAV (`MoveSeries`, `Shift`) and Graph (`GraphRecurrence.EditSeries`; added 2026-11-11).
- F5. A custom VTIMEZONE body is probably not used for expansion (Ical.Net resolves the TZID by name); confirm with a test and honour it (R3).
- F6. R6 waits behind a running network sync and does not refresh the tray flyout or an open editor (R6 sub-bullet).

## Build slices

Each slice is built, tested and checked in the app before the next starts.

1. Fixes F1–F6, and tests: a weekly 09:00 Sydney series across DST in both backends, unknown TZIDs and Graph custom zones, separate end zones, the occurrence index after a zone change. Built 2026-11-11 (package 1.0.17.0) with automated tests passing; in-app checks skipped by the user, not verified (timezone change while open, Graph all-day after a zone change, separate end zone in Outlook, Outlook custom zone, CalDAV custom VTIMEZONE, series move across March).
2. Display: R9, R8 (ruler, Settings > Appearance, and the per-set ruler of [calendar-sets](calendar-sets.md) R11). Built and checked in the app 2026-11-11 (package 1.0.21.0).
3. R7 for single events and whole series, with the separate end timezone. Built 2026-11-11 (package 1.0.22.0, installed) with automated tests passing; in-app checks deferred by the user (New York and Sydney→London events in Outlook and a CalDAV client, series zone change with lost occurrences, floating and custom zones kept on a title edit, the editor's pickers and caption, undo of a zone change).
4. R7 for this occurrence and this and following. Built 2026-11-11 on top of slice 3 before its checks, by the user's choice (same package 1.0.22.0, installed), with automated tests passing; in-app checks pending (one occurrence and this-and-following zone changes in both backends, with a separate end zone; the listed-dates refusal; floating and custom zones kept on one occurrence's title edit).

## Out of scope

- Changing the display timezone independently of Windows ("travel mode"). While travelling, new events default to the timezone Windows is set to.
- Timezones for task due dates (date-only, [tasks](tasks.md) R2a).
- More than one secondary timezone.
- Timezones typed in quick add (e.g. "3pm EST").

## Acceptance criteria

- [ ] A weekly 09:00 Australia/Sydney event stays at 09:00 local across the DST change, in both backends.
- [ ] An event created in the app with timezone America/New_York shows the correct time in Outlook and in another CalDAV client.
- [ ] All-day events never shift a day when the system timezone changes.
- [x] The secondary timezone column aligns correctly with the primary ruler; in a week with a DST change in either zone, it follows today's offset (or the first visible day's) and notes the day the offset changes.
- [ ] An event with a separate end timezone (e.g. Sydney to London) shows the right start and end in Outlook and in another CalDAV client.
- [ ] Changing the timezone of one occurrence, and of "this and following", keeps the other occurrences unchanged, in both backends.

## Dependencies

- [sync-ms365](sync-ms365.md), [sync-radicale](sync-radicale.md), [recurring-events](recurring-events.md), [calendar-views](calendar-views.md)

## Decisions

- 2026-11-11: the secondary ruler uses today's offset (else the first visible day's) and notes a mid-week offset change, rather than per-day labels (user's choice).
- 2026-11-11: the whole of R7, including the occurrence and this-and-following scopes, is in scope, built as slices 3 and 4 (user's choice).
- 2026-11-11: a time edit on an MS365 event in a custom or unknown zone (e.g. `tzone://Microsoft/Custom`) writes UTC, keeping the exact times; Graph takes no custom zone on a write, so the zone itself is lost and a series' local time moves by an hour across DST (user's choice over the Windows zone).
- 2026-11-11: the secondary ruler (on/off and zone) is saved per calendar set ([calendar-sets](calendar-sets.md) R11); the display timezone stays the Windows timezone (user's choice).
- 2026-11-11: the editor's time pickers show the event's own zone, with the local time as a caption, as in Outlook and Google Calendar (user's choice).
- 2026-11-11: a whole-series zone change on a CalDAV series with listed dates (RDATE) is refused, like moving such a series; known limit (user's choice). The same holds for a this-and-following zone change on such a series; one occurrence's zone can still be changed.
- 2026-11-11: a CalDAV this-and-following zone change does not ask about lost changed occurrences, as other CalDAV splits don't; an override is lost only when its time falls in an hour the new zone skips.
