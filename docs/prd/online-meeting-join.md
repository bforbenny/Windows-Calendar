---
description: Detect Teams/Zoom/Meet/Webex links on events and show a one-click Join button
when-to-use: Before working on online-meeting link detection, the Join button, or Join actions in the tray flyout and reminder toasts
tags: [events, online-meeting, teams, ui]
status: pending
---

# Online meeting join

## Summary

Events that carry an online-meeting link show a Join button that opens the meeting in its own app or browser. The link comes from Graph's meeting data, or is found in the event's location or notes. Creating meeting links is not included.

## Requirements

- R1. Link sources, in priority order:
  - Graph: `onlineMeeting.joinUrl` (when `isOnlineMeeting` is true), then `onlineMeetingUrl`.
  - CalDAV: the `URL` property, then `X-GOOGLE-CONFERENCE` / `X-MICROSOFT-SKYPETEAMSMEETINGURL` if present.
  - Both: the first recognised link in `location`, then in the description.
- R2. Recognised providers (host match, HTTPS only): Microsoft Teams (`teams.microsoft.com`, `teams.live.com`), Zoom (`zoom.us`, `*.zoom.us`), Google Meet (`meet.google.com`), Webex (`*.webex.com`). Other links are not treated as join links.
- R3. The detected link is computed at sync/save time and stored with the item, so it works offline and in the views without re-parsing.
- R4. A Join button (with the provider name) appears in:
  - event details / editor;
  - the event block in Day/Week/Agenda when the event is in progress or starts within 10 minutes;
  - the [tray-flyout](tray-flyout.md);
  - the reminder toast as a "Join" action (see [reminders-notifications](reminders-notifications.md) R3).
- R5. Join opens the URL with the Windows default handler (`Launcher.LaunchUriAsync`), so Teams/Zoom desktop apps take over when installed. Only `https://` URLs that passed R2 are launched.
- R6. Keyboard: Ctrl+J joins the selected event; from anywhere in the app, Ctrl+Shift+J joins the meeting in progress or next starting within 10 minutes; if more than one qualifies, a small picker lists them.
- R6a. Cancelled and declined meetings never show Join ([attendees-invitations](attendees-invitations.md) R4–R5).
- R7. The join URL is not copied when an event is duplicated (see [duplicate-event](duplicate-event.md) R4).

## Out of scope

- Creating Teams/online meetings (`isOnlineMeeting` = true on create).
- Dial-in numbers and meeting passcodes.
- Providers beyond R2.

## Acceptance criteria

- [ ] A Teams meeting from Outlook shows Join, and clicking it opens Teams.
- [ ] A Radicale event with a Zoom link in its location or notes shows Join.
- [ ] A non-HTTPS or unrecognised link never shows Join.
- [ ] The reminder toast and the tray flyout both offer Join for a meeting with a link.

## Dependencies

- [sync-ms365](sync-ms365.md), [sync-radicale](sync-radicale.md), [calendar-views](calendar-views.md), [reminders-notifications](reminders-notifications.md), [tray-flyout](tray-flyout.md)
