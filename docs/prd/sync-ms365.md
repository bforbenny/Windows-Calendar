---
description: Two-way sync of MS365 calendars (events) and Microsoft To Do task lists via Microsoft Graph
when-to-use: Before working on anything that calls Microsoft Graph
tags: [sync, ms365, graph, delta, todo]
status: in-progress
---

# MS365 sync (Microsoft Graph)

## Summary

Each MS365 account syncs its calendars and To Do task lists through Microsoft Graph v1.0 using delta queries, two-way. The offline window for events is a rolling **-3 months / +12 months** from today.

## Requirements

- R1. Calendars: discover with `GET /me/calendars`. Per calendar, sync with `GET /me/calendars/{id}/calendarView/delta?startDateTime&endDateTime`.
- R2. **Window chunking:** split the -3/+12 month window into chunks of ≤ 30 days, each with its own deltaLink. Reason: there are 2026 reports of calendarView/delta paging forever (no `@odata.deltaLink` returned) on large windows.
- R3. **Loop guard:** if a delta round exceeds N pages or returns repeated identical page contents, discard that chunk's state and restart it with a fresh initial delta. Log it (no tokens in the log). An expired delta token (HTTP 410 or `syncStateNotFound`, e.g. after a long outage) restarts the chunk the same way.
- R4. Rolling window: daily, drop chunks that fall out of the past bound and add new chunks at the future bound.
- R5. Outside the window: when online, viewing a date outside the window fetches `calendarView` (non-delta) on demand into a transient cache.
- R6. Recurring series: calendarView returns occurrences/exceptions; fetch the `seriesMaster` (`GET /me/events/{seriesMasterId}`) so the series rule can be edited. See [recurring-events](recurring-events.md).
- R7. Writes: `POST /me/calendars/{id}/events`, `PATCH /me/events/{id}`, `DELETE /me/events/{id}`. Optimistic concurrency via `If-Match` with the item's `@odata.etag`; HTTP 412 → [conflict-resolution](conflict-resolution.md).
- R8. Timezones: send `Prefer: outlook.timezone="UTC"` for reads; writes carry the event's own timezone. See [timezone-handling](timezone-handling.md).
- R9. To Do: `GET /me/todo/lists`, then per list `GET /me/todo/lists/{id}/tasks/delta`. Writes via `/me/todo/lists/{id}/tasks`. Special lists (e.g. `wellknownListName = flaggedEmails`) are shown read-only. See [tasks](tasks.md).
- R10. Throttling: honour HTTP 429/503 `Retry-After`; global backoff per account.
- R11. 401 / `invalid_grant`, or 403 after consent is revoked → mark the account "auth required"; do not loop. While the window is hidden or starting minimised, show one toast and the red Sign in bar ([ui-states](ui-states.md) R2); the interactive WAM prompt opens only when the user clicks Sign in.
- R12. Store the Graph JSON payload as-is for round-trip safety; PATCH only the fields that changed, so fields the app does not show (categories, sensitivity, attachments, HTML body) are kept. Store `webLink` for Open in Outlook ([attendees-invitations](attendees-invitations.md) R6).

## Interim (first slice: sign-in and pull of events)

- Events are pulled only; MS365 events open view-only in the editor and cannot be dragged. Writes (R7), series masters (R6), on-demand fetch outside the window (R5) and To Do (R9) come in later slices.
- R1: calendars from `/me/calendars` feed re-discovery ([account-management](account-management.md) R11); `canEdit` false makes a calendar read-only, `isDefaultCalendar` marks the default.
- R2, R4: the window is split into fixed 30-day chunks (aligned to a fixed date, so they stay the same from day to day), each with its own deltaLink in the collection's sync state. Each sync drops chunks that left the window and adds new ones. Pages are requested with `Prefer: odata.maxpagesize=50`.
- Each chunk keeps the set of event ids it returned. An event leaves the store only when no chunk of its calendar still holds it, so an event spanning two chunks, or moved from one to another, is not lost.
- R3: a chunk restarts from a fresh initial delta when a round passes 200 pages, when a page repeats the previous one, or on HTTP 410 / `syncStateNotFound`. Ids it no longer returns are then removed as above. Only the calendar's local id and the reason are logged.
- Each occurrence and exception from calendarView is its own item (`IsRecurring` set); cancelled events are removed. The Graph JSON is stored as-is with its `@odata.etag` (R12).
- R8: reads use `Prefer: outlook.timezone="UTC"`. All-day events are dates in the Windows timezone; the occurrence index reads MS365 event times from the payload, so a Windows timezone change moves them.
- R10: a 429 or 503 sets a backoff for the account from `Retry-After` (60 seconds when missing); the account is skipped until then.
- R11 interim: a 401, or MSAL needing interaction, pauses the account until restart, with a log warning. The toast and Sign in bar come with [ui-states](ui-states.md).
- Access tokens are fetched silently for each sync run, used for that run only and never logged.

## Interim (second slice: series masters and event writes)

- R6: calendarView delta returns occurrences as stubs (id, start, end, seriesMasterId). Series masters are now stored: kept from delta when it returns them, otherwise fetched with `GET /me/events/{seriesMasterId}` (the number fetched is logged). A master is not drawn itself; an occurrence without its own subject or location shows its master's. Each occurrence and exception records its master's id.
- Reads ask for plain-text bodies (`Prefer: outlook.body-content-type="text"`). The chunk state is reset once by this slice, so every calendar is pulled again and gets its masters.
- R7: queued changes are sent before the pull, in outbox order. Create is `POST /me/calendars/{id}/events`; the reply gives the event its Graph id, etag and stored JSON. Update is `PATCH /me/events/{id}`, delete is `DELETE /me/events/{id}`, both with `If-Match`. A 412 or 404 is a conflict ([conflict-resolution](conflict-resolution.md)): the event is fetched again and the server version wins. A 429 or 503 backs the account off (R10); a 5xx or 408 is retried later; any other 4xx waits for the user.
- R12: an edit remembers which fields it changed (subject, start/end and all-day, location, body); the PATCH sends only those. A changed location is sent as `location.displayName`. The description is always editable: unchanged it is never sent; changed, it replaces the body as plain text.
- R8 for writes: a new event is written in the Windows timezone; an edited event keeps its own timezone (`originalStartTimeZone`) when Windows knows it, else the Windows timezone. All-day events are written as midnight-to-midnight dates with `isAllDay`.
- Editable in the app: create, edit, delete, drag, move to another calendar (a create in the new calendar, then a delete, one linked group), undo, and the recurring operations in [recurring-events](recurring-events.md). Calendars with `canEdit` false stay read-only.
- Meetings stay view-only: an event (or the series of an occurrence) with attendees, or one you did not organise, cannot be edited, dragged or deleted from the app, so the app never sends meeting updates or cancellations.

## Out of scope

- Graph change notifications (webhooks) — they need a public endpoint; polling is used instead.
- Sending invitations, responding to them, and editing attendees. Attendees are shown read-only ([attendees-invitations](attendees-invitations.md)).
- Displaying or editing categories, sensitivity and attachments (preserved by R12).
- Teams meeting creation.

## Acceptance criteria

- [ ] First sync of a calendar with ≥ 500 events in the window completes without looping.
- [ ] An event created in Outlook appears within one sync cycle (≤ 5 min); an edit made in the app appears in Outlook.
- [ ] Deleting in either place removes it in the other.
- [ ] Simulated 429 response is honoured with `Retry-After`.
- [ ] To Do task created in the app appears in Microsoft To Do and vice versa.

## Dependencies

- [account-management](account-management.md), [local-store-offline](local-store-offline.md), [conflict-resolution](conflict-resolution.md), [recurring-events](recurring-events.md), [timezone-handling](timezone-handling.md)

## Open questions

- Exact page-count threshold for the loop guard (R3) — set during implementation from observed behaviour.
