---
description: Read-only attendee list, "needs response" marker, Open in Outlook, and guards on meetings you organise
when-to-use: Before working on events that have attendees, invitations, cancelled or declined meetings, or deleting/editing meetings
tags: [events, attendees, invitations, ms365, safety]
status: in-progress
---

# Attendees and invitations

## Summary

The app shows who is invited to a meeting and whether you still need to respond, but responding happens in Outlook through an "Open in Outlook" button. Meetings you organise are guarded, because deleting or changing them can notify attendees, which cannot be undone.

## Requirements

- R1. Attendee list: the editor shows the organiser and attendees read-only, each with name (or address) and response (Accepted / Tentative / Declined / No response). Sources: Graph `organizer`, `attendees[].status`; CalDAV `ORGANIZER`, `ATTENDEE` with `PARTSTAT`. Attendees cannot be added, removed or edited.
- R2. Your role on an event:
  - Graph: organiser when `isOrganizer` is true, otherwise attendee.
  - CalDAV: attendee when `ORGANIZER` is present and differs from the account's own address (an optional "Email address" field on the Radicale account); otherwise organiser. With no address set, every event counts as your own.
  - "Meeting with attendees" means an event with at least one attendee other than you.
- R3. Needs response: Graph events where you are an attendee and `responseStatus.response` is `none` or `notResponded` show a "needs response" glyph on the card and "You have not responded" in the editor. This uses an icon and text, not colour alone ([ui-colour-theme](ui-colour-theme.md) R4).
- R4. Declined events (`responseStatus.response = declined`; CalDAV `PARTSTAT=DECLINED` for your address) are hidden from Day/Week/Month/Agenda and the [tray-flyout](tray-flyout.md), get no reminders and no Join. They stay findable in [search](search.md).
- R5. Cancelled meetings (Graph `isCancelled`; CalDAV `STATUS:CANCELLED`) show with the title struck through and a "Cancelled" label, with no Join button and no reminder. The user can delete them ("Remove from calendar").
- R6. Open in Outlook: MS365 events have an "Open in Outlook" button in the editor, which launches the Graph `webLink` with the Windows default handler. It is the route for responding to invitations, proposing a new time, and editing what the app treats as read-only (R7, R8, [calendar-views](calendar-views.md) R6 HTML descriptions). The link is stored with the item, but needs a connection to open.
- R7. Meetings where you are an attendee are read-only ([calendar-views](calendar-views.md) R9), except your own reminder, which stays editable. Deleting one shows "Remove from your calendar? The organiser is not told." and asks for confirmation (verify Graph behaviour on attendee delete during implementation).
- R8. Meetings with attendees that you organise (CLAUDE.md security rule 5):
  - Delete asks "Cancel this meeting and notify N attendees?" with Cancel meeting / Keep. There is no undo banner, and the cancellation is not on the Ctrl+Z stack, since it cannot be recalled ([undo](undo.md) R1, R7).
  - The first change in an editing session, or a drag, to time, date, recurrence, location or title asks once "Send update to N attendees?". The answer applies until the panel closes, and is combined with the recurring scope prompt ([calendar-views](calendar-views.md) R6d) into one dialog.
  - "This and following" is not offered, because the new series would have no attendees ([recurring-events](recurring-events.md) R6). The dialog points to Open in Outlook.
  - Moving to another calendar or account is not offered.
  - Radicale meetings with attendees: the app does no scheduling ([sync-radicale](sync-radicale.md)), so the delete and change prompts say "Attendees are not notified" instead.
- R9. The Graph behaviour assumed in R8 (organiser delete sends cancellations; PATCH of time or location sends updates) is verified during implementation; the guards stay either way.

## Interim (declined flag)

- 2026-11-12: the store keeps a declined flag per event (Graph `responseStatus.response = declined`; CalDAV `PARTSTAT=DECLINED` on the attendee matching the account's address), set when an event is read and once for existing events after the upgrade. Only the clash and gap check uses it so far ([schedule-gap-warning](schedule-gap-warning.md) R2); hiding declined events (R4) is still to be built.

## Interim (muted invitations, MS365)

- R3, first part: MS365 events where you are an attendee (not the organiser) and `responseStatus.response` is `none`, `notResponded` or `tentativelyAccepted` are drawn muted in Day, Week, Month and Agenda: no fill (the view's background), a 1 epx border and the left bar in the calendar colour, normal text. An occurrence of such a series follows its series. High contrast keeps the same outline with system colours.
- The "needs response" glyph and the editor's "You have not responded" text come later; until then the muted style is the only marker.
- Replaced by [event-show-as](event-show-as.md) R5 (2026-11-06, branch `event-show-as`): the muted outline looked the same as Free, so unanswered invitations are now drawn by their show-as, which is usually Tentative. Until the "needs response" glyph is built, an unanswered invitation has no marker of its own.

## Interim (meeting test and Open in Outlook, MS365)

- 2026-11-12, R2: an MS365 event is a meeting (view-only, except your reminder) when you did not organise it (`isOrganizer` false) or it has an attendee whose address differs from the organiser's. An event you organised with only yourself as attendee is your own event and can be edited. Stored events are read again once on upgrade, so this takes effect without a re-sync.
- 2026-11-12, R6, first part: the editor's notice for a view-only MS365 meeting ("Meetings can't be changed from this app yet, except your reminder.") has an "Open in Outlook" link on the line under it, which opens the event's Graph `webLink` (an occurrence without its own uses its series') with the Windows default handler. Only an HTTPS link on an Outlook host (`outlook.office365.com`, `outlook.office.com`, `outlook.live.com`) is opened; the link is not logged. The button on every MS365 event comes later.

## Out of scope

- Accept / Tentative / Decline and Propose new time in the app (done in Outlook via R6).
- Adding or removing attendees, sending invitations, creating meetings.
- iTIP/iMIP and .ics invitation files.
- Free/busy lookup and a scheduling assistant.
- Notifications about new invitations or changes made by others.

## Acceptance criteria

- [ ] An Outlook meeting shows its attendees and their responses in the editor.
- [ ] An unanswered invitation shows the "needs response" glyph; after accepting it in Outlook, the glyph disappears within one sync.
- [ ] A declined meeting does not appear in the views or the tray flyout.
- [ ] Deleting a meeting you organise asks before sending the cancellation; choosing Keep changes nothing.
- [ ] "This and following" is not offered on a recurring meeting you organise that has attendees.
- [ ] Open in Outlook opens the event in Outlook on the web or desktop.

## Dependencies

- [calendar-views](calendar-views.md), [sync-ms365](sync-ms365.md), [sync-radicale](sync-radicale.md), [recurring-events](recurring-events.md), [undo](undo.md), [reminders-notifications](reminders-notifications.md), [online-meeting-join](online-meeting-join.md)
