---
description: Two-way sync of MS365 calendars (events) and Microsoft To Do task lists via Microsoft Graph
when-to-use: Before working on anything that calls Microsoft Graph
tags: [sync, ms365, graph, delta, todo]
status: pending
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
