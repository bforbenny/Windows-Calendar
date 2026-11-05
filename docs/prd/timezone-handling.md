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

- R1. Internal representation: store instants in UTC, plus the event's original timezone ID (IANA canonical) and the floating/all-day flag. All-day events are date-only and are never timezone-shifted.
- R2. Conversion: use .NET `TimeZoneInfo` with IANA ↔ Windows ID conversion (`TimeZoneInfo.TryConvertIanaIdToWindowsId` / reverse, ICU on Windows 11). Graph writes use the Windows ID; CalDAV writes use the IANA TZID with a matching VTIMEZONE.
- R3. Custom/non-standard VTIMEZONE definitions from the server are honoured for expansion (Ical.Net), and the original definition is preserved on round-trip.
- R4. Recurrence expansion happens in the event's own timezone (not UTC), so DST transitions keep the wall-clock time.
- R5. Floating times (CalDAV, no TZID) are interpreted in the display timezone and are kept floating on write.
- R6. Display timezone = Windows system timezone. The app reacts to system timezone changes without a restart: it regenerates the occurrence index ([local-store-offline](local-store-offline.md) R3) and reschedules reminders ([reminders-notifications](reminders-notifications.md) R2).
- R7. **Per-event timezone:** the event editor has a timezone picker (default: system timezone), with an optional separate end timezone where both backends support it. The picker is searchable by city/name.
- R8. **Secondary timezone column:** an optional second time ruler in the Day/Week views showing a user-chosen timezone, with a label (e.g. "UTC", "London"). Toggle and timezone are set in Settings > Appearance ([settings-page](settings-page.md) R2).
- R9. Events whose timezone differs from the display timezone show their original-timezone time in the event details.

## Out of scope

- Changing the display timezone independently of Windows ("travel mode"). While travelling, new events default to the timezone Windows is set to.
- Timezones for task due dates (date-only, [tasks](tasks.md) R2a).
- More than one secondary timezone.

## Acceptance criteria

- [ ] A weekly 09:00 Australia/Sydney event stays at 09:00 local across the DST change, in both backends.
- [ ] An event created in the app with timezone America/New_York shows the correct time in Outlook and in another CalDAV client.
- [ ] All-day events never shift a day when the system timezone changes.
- [ ] The secondary timezone column aligns correctly with the primary ruler, including across a DST change in either zone.

## Dependencies

- [sync-ms365](sync-ms365.md), [sync-radicale](sync-radicale.md), [recurring-events](recurring-events.md), [calendar-views](calendar-views.md)
