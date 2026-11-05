---
description: Read-only attendee list, "needs response" marker, Open in Outlook, and guards on meetings you organise
when-to-use: Before working on events that have attendees, invitations, cancelled or declined meetings, or deleting/editing meetings
tags: [events, attendees, invitations, ms365, safety]
status: pending
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
