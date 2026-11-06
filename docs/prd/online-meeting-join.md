---
description: Detect Teams/Zoom/Meet/Webex links on events and show a one-click Join button
when-to-use: Before working on online-meeting link detection, the Join button, or Join actions in the tray flyout and reminder toasts
tags: [events, online-meeting, teams, ui]
status: in-progress
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

## Interim (first slice)

- Built: R1, R2, R3, R5, R6, R6a (declined MS365 meetings; cancelled events are not stored) and R7. R4 is built for the editor and for Day, Week and Agenda cards.
- R4 for the tray flyout and the reminder toast comes with [tray-flyout](tray-flyout.md) and [reminders-notifications](reminders-notifications.md). Neither exists yet.
- Editor: a "Join Teams meeting" (or Zoom, Google Meet, Webex) button at the top of the form, also on read-only meetings. A link typed into the location or description shows the button once the field loses focus.
- Cards: a small "Join" button, filled in the accent colour so it stands out on the tinted card, at the end of the title line, shown from 10 minutes before the start until the end. It is not shown on cards too short for text, on Month chips or in the all-day lane. The provider is named in its tooltip and screen-reader name, because the narrow columns have no room for it. The views redraw just after each clock minute when an event enters or leaves that window.
- Ctrl+Shift+J works from anywhere except a text field. With no meeting it says so. With several it shows a menu of them. Ctrl+J needs the event selected in the view.
- An MS365 occurrence without its own link uses its series' link.
- R7: duplicating a Radicale event drops URL, X-GOOGLE-CONFERENCE and X-MICROSOFT-SKYPETEAMSMEETINGURL when they hold a join link. Other URLs are kept. MS365 duplicates never carried the online meeting. A link written in the location or description is the user's text and is copied, so the copy also shows Join.
- The join URL is not written to the log, because it may carry a passcode.
- Existing events get their link from the stored data once, on the first start after the update (store v12).

## Pending user check

Built on branch `online-meeting-join` (2026-11-06). The branch is stacked on `event-show-as`. Nothing here is verified in the app or merged yet:

- [x] A Teams meeting from Outlook shows Join in the editor, and on its card from 10 minutes before the start. Clicking it opens Teams. This also confirms that calendarView delta returns `onlineMeeting`.
- [x] A Radicale event with a Zoom link in its location or notes shows Join.
- [x] An `http://` link or an unrecognised link never shows Join.
- [x] Ctrl+J on a selected meeting, and Ctrl+Shift+J with zero, one and two meetings due.
- [x] Clicking a card's Join button neither opens the editor nor starts a drag.
- [x] A Join button appears on its own at the 10-minute mark without any other action.

## Out of scope

- Creating Teams/online meetings (`isOnlineMeeting` = true on create).
- Dial-in numbers and meeting passcodes.
- Providers beyond R2.

## Acceptance criteria

- [x] A Teams meeting from Outlook shows Join, and clicking it opens Teams.
- [x] A Radicale event with a Zoom link in its location or notes shows Join.
- [x] A non-HTTPS or unrecognised link never shows Join.
- [ ] The reminder toast and the tray flyout both offer Join for a meeting with a link.

## Dependencies

- [sync-ms365](sync-ms365.md), [sync-radicale](sync-radicale.md), [calendar-views](calendar-views.md), [reminders-notifications](reminders-notifications.md), [tray-flyout](tray-flyout.md)
