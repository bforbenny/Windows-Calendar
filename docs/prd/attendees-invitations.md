---
description: Meetings and invitations - which events are meetings, declined and cancelled meetings drawn faded, and Open in Outlook for responding and changing meetings
when-to-use: Before working on events that have attendees, invitations, cancelled or declined meetings, or deleting/editing meetings
tags: [events, attendees, invitations, ms365, safety]
status: closed
---

# Attendees and invitations

## Summary

The app does not respond to invitations or change Outlook meetings. Meetings are view-only (except your own reminder), and an "Open in Outlook" link takes you to Outlook to respond, change or delete them, so the app never sends updates or cancellations to attendees. Declined and cancelled meetings stay on the calendar, drawn faded.

## Requirements

- R1. Withdrawn 2026-11-12: no attendee list in the editor (see Out of scope).
- R2. Your role on an event:
  - Graph: an event is a meeting when you did not organise it (`isOrganizer` false), or it has an attendee whose address differs from the organiser's. An event you organised with only yourself as attendee is your own event.
  - CalDAV: attendee when `ORGANIZER` is present and differs from the account's own address (an optional "Email address" field on the Radicale account); otherwise organiser. With no address set, every event counts as your own.
- R3. Needs response: when you are an attendee of an MS365 meeting and `responseStatus.response` is `none` or `notResponded`, the editor's notice says "You have not responded." and the Open in Outlook link (R6) is under it. You respond in Outlook. There is no marker on the card; the card is drawn by its show-as, usually Tentative ([event-show-as](event-show-as.md) R5).
- R4. Declined meetings (Graph `responseStatus.response = declined` when you are not the organiser; CalDAV `PARTSTAT=DECLINED` on the attendee with the account's address) stay in Day, Week, Month, Agenda and the [tray-flyout](tray-flyout.md), drawn faded (R5a). They get no reminder and no Join, and are left out of the clash and gap check ([schedule-gap-warning](schedule-gap-warning.md) R2). The editor's notice says "You declined this meeting."
- R5. Cancelled meetings (Graph `isCancelled`; CalDAV `STATUS:CANCELLED` on an event that does not repeat) stay in the same places, drawn faded (R5a) with the title struck through. No reminder, no Join, not in the clash and gap check. The editor's notice says "This meeting was cancelled." An MS365 one is removed in Outlook (R6); a Radicale one can be deleted in the app like any event. A cancelled occurrence inside a repeating Radicale series stays hidden, as before.
  - R5a. Faded: the card keeps its show-as style (fill, bar, border, glyph) at about half strength, text included. Exception to [ui-colour-theme](ui-colour-theme.md) R4 and the WCAG AA text contrast in [event-show-as](event-show-as.md) R5, chosen by the user on 2026-11-12: a declined meeting is marked by fading alone, with no icon or label; a cancelled one also has the struck-through title. The screen-reader name and tooltip add "Declined" or "Cancelled".
- R6. Open in Outlook: the editor's notice for a view-only MS365 meeting has an "Open in Outlook" link on the line under it. It opens the event's Graph `webLink` (an occurrence without its own uses its series') with the Windows default handler. Only an HTTPS link on an Outlook host (`outlook.office365.com`, `outlook.office.com`, `outlook.live.com`) is opened; the link is not logged. It needs a connection to open.
- R7. MS365 meetings, whether you organise them or are invited, are view-only, except your own reminder ([reminders-notifications](reminders-notifications.md) R5a). They cannot be edited, dragged, moved or deleted in the app; changes, responses and deletes are made in Outlook through R6. The app therefore never sends meeting updates or cancellations.
- R8. Radicale meetings stay editable: the app does no scheduling there ([sync-radicale](sync-radicale.md)), so nothing is sent to attendees. "This and following" is not offered on a repeating Radicale meeting you organise ([recurring-events](recurring-events.md) R6).
- R9. Withdrawn 2026-11-12: with R7, the app sends no meeting updates or cancellations, so there is no Graph behaviour to verify.

## Interim (declined flag)

- 2026-11-12: the store keeps a declined flag per event (Graph `responseStatus.response = declined`; CalDAV `PARTSTAT=DECLINED` on the attendee matching the account's address), set when an event is read and once for existing events after the upgrade. Only the clash and gap check uses it so far ([schedule-gap-warning](schedule-gap-warning.md) R2); R4's faded drawing is built on it.

## Interim (muted invitations, MS365)

- R3, first part: MS365 events where you are an attendee (not the organiser) and `responseStatus.response` is `none`, `notResponded` or `tentativelyAccepted` are drawn muted in Day, Week, Month and Agenda: no fill (the view's background), a 1 epx border and the left bar in the calendar colour, normal text. An occurrence of such a series follows its series. High contrast keeps the same outline with system colours.
- Replaced by [event-show-as](event-show-as.md) R5 (2026-11-06, branch `event-show-as`): the muted outline looked the same as Free, so unanswered invitations are now drawn by their show-as, which is usually Tentative.

## Interim (meeting test and Open in Outlook, MS365)

- 2026-11-12, package 1.0.32.0: R2 (Graph) and R6 built and checked by the user. Stored events were read again once on upgrade, so the R2 change took effect without a re-sync.

## Implementation notes

- 2026-11-12: cancelled MS365 events were dropped during sync until now. Keeping them (R5) resets the Graph chunk state once, so every calendar is pulled again and already-cancelled meetings appear.

## Verification

- 2026-11-12: package 1.0.33.0 checked by the user (R3, R4, R5, R7, tray flyout).

## Out of scope

- An attendee list in the editor (R1 withdrawn).
- Accept / Tentative / Decline and Propose new time in the app (done in Outlook via R6).
- Changing or deleting MS365 meetings in the app, including the "Send update to N attendees?" and "Cancel this meeting?" prompts (done in Outlook via R6).
- Adding or removing attendees, sending invitations, creating meetings.
- iTIP/iMIP and .ics invitation files.
- Free/busy lookup and a scheduling assistant.
- Notifications about new invitations or changes made by others.

## Acceptance criteria

- [x] An unanswered invitation's editor says "You have not responded." with Open in Outlook under it; after accepting it in Outlook, the text is gone within one sync.
- [x] A declined meeting shows faded in Week, Month, Agenda and the tray flyout, with no reminder, no Join and no clash warning; the editor says "You declined this meeting."
- [x] A meeting cancelled by its organiser shows faded with its title struck through, with no reminder, no Join and no clash warning; the editor says "This meeting was cancelled."
- [x] An MS365 meeting you organise with other attendees opens view-only with Open in Outlook, and cannot be dragged or deleted.
- [x] Open in Outlook opens the event in Outlook on the web or desktop.

## Dependencies

- [calendar-views](calendar-views.md), [sync-ms365](sync-ms365.md), [sync-radicale](sync-radicale.md), [recurring-events](recurring-events.md), [reminders-notifications](reminders-notifications.md), [online-meeting-join](online-meeting-join.md), [schedule-gap-warning](schedule-gap-warning.md), [event-show-as](event-show-as.md)
