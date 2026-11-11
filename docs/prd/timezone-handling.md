---
description: Correct timezone and DST handling across Graph and CalDAV, per-event timezone selection, and a secondary timezone column
when-to-use: Before working on date/time storage, display, conversion, or any timezone UI
tags: [timezone, dst, datetime, ui]
status: pending
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
- R7. **Per-event timezone:** the event editor has a timezone picker (default: system timezone), searchable by city/name (the Windows timezone display names, e.g. "(UTC+10:00) Canberra, Melbourne, Sydney"; no new package). Hidden for all-day events.
  - Separate end timezone: a "Separate end timezone" toggle shows a second picker. It maps to Graph `end.timeZone` and CalDAV `DTEND;TZID`. An event from sync whose end zone differs opens with the toggle on.
  - A floating CalDAV event shows "Floating (no timezone)" and a Graph custom zone (e.g. `tzone://Microsoft/Custom`) shows "Custom (kept)" as an extra entry; either is kept unless the user picks another zone.
  - Allowed at any edit scope ([recurring-events](recurring-events.md)): whole series (exception dates, `UNTIL` and Graph `recurrenceTimeZone` converted; changed occurrences that cannot be kept are listed as lost, as a repeat-rule change does), this occurrence (the occurrence gets its own zone: a CalDAV override with its own `DTSTART;TZID`, a Graph instance PATCH with its own `start.timeZone`), this and following (the series splits and the new series uses the new zone).
  - `EventFields` carries the start and end zones through the CalDAV, Graph, recurrence and transfer code, which today all use the Windows timezone.
- R8. **Secondary timezone column:** an optional second time ruler in the Day/Week views showing a user-chosen timezone, with a label (e.g. "UTC", "London"). Toggle and timezone are set in Settings > Appearance ([settings-page](settings-page.md) R2).
- R9. Events whose timezone differs from the display timezone show their original-timezone time in the event details.

## Current state (2026-11-11)

Already in code, to be audited rather than rebuilt: R2 (Graph reads each event's own zone; CalDAV writes keep the event's TZID and add a VTIMEZONE; transfer converts IANA ↔ Windows), R4 (Ical.Net expands in the event's zone), R5 (floating times use the Windows timezone), part of R3 (an edit keeps the existing TZID), and part of R6 (the occurrence index is rebuilt for a new timezone on the next sync run). Not built: R7, R8, R9 and the immediate R6 reaction.

## Out of scope

- Changing the display timezone independently of Windows ("travel mode"). While travelling, new events default to the timezone Windows is set to.
- Timezones for task due dates (date-only, [tasks](tasks.md) R2a).
- More than one secondary timezone.
- Timezones typed in quick add (e.g. "3pm EST").

## Acceptance criteria

- [ ] A weekly 09:00 Australia/Sydney event stays at 09:00 local across the DST change, in both backends.
- [ ] An event created in the app with timezone America/New_York shows the correct time in Outlook and in another CalDAV client.
- [ ] All-day events never shift a day when the system timezone changes.
- [ ] The secondary timezone column aligns correctly with the primary ruler, including across a DST change in either zone.
- [ ] An event with a separate end timezone (e.g. Sydney to London) shows the right start and end in Outlook and in another CalDAV client.
- [ ] Changing the timezone of one occurrence, and of "this and following", keeps the other occurrences unchanged, in both backends.

## Dependencies

- [sync-ms365](sync-ms365.md), [sync-radicale](sync-radicale.md), [recurring-events](recurring-events.md), [calendar-views](calendar-views.md)
