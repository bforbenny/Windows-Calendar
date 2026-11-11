---
description: Add a colleague's MS365 primary calendar by email address and sync it read-only, for calendars Graph's /me/calendars does not list
when-to-use: Before working on colleague (shared) MS365 calendars, the Calendars.Read.Shared permission, or MS365 calendar re-discovery
tags: [ms365, graph, sync, shared-calendars, accounts]
status: closed
---

# MS365 colleague calendars

## Summary

Colleague calendars added in Outlook through "Add calendar → Add from directory" are not returned by Graph's `/me/calendars` or by `/me/calendarGroups/{id}/calendars`, so the app cannot discover them. The app lets the user add a colleague's primary calendar by email address, reads it directly from the colleague's mailbox (`/users/{email}/calendar`), and syncs it read-only with the existing chunked delta engine ([sync-ms365](sync-ms365.md) R2–R4).

## Requirements

- R1. Add: the MS365 account's sidebar menu has "Add colleague's calendar…". It asks for an email address and checks it with `GET /users/{email}/calendar?$select=id,name,owner`, then reads one day of events (`GET /users/{email}/calendarView`, `$top=1`). On success the calendar is added and a sync is requested. If the calendar is not found or refused, a message says the address is wrong or the user has no access; if only the events are refused, a message says the colleague shares only free/busy (or nothing) with the user. Nothing is added in either case.
- R2. Duplicates are refused with a message: an email already added to this account, or one equal to the `owner` address of a calendar `/me/calendars` already lists (read with `/me/calendars?$select=owner` when adding, so syncs are unchanged).
- R3. Storage: a calendar of the account (`kind` Calendar), `remote_ref` `user:<email, lower case>`, read-only, named after the owner's display name (else the email). It gets an automatic colour and shape and joins only "All" ([calendar-sets](calendar-sets.md) R9). Only the email is stored; no tokens.
- R4. Re-discovery ([account-management](account-management.md) R11) keeps colleague calendars: they are added to the discovered list before it is compared, so a sync never removes them.
- R5. Sync: each colleague calendar is pulled with `GET /users/{email}/calendarView/delta`, with the same 30-day chunks, deltaLinks, loop guard and window as R2–R4 of sync-ms365, and series masters fetched with `GET /users/{email}/events/{id}`. It is pulled in parallel with the account's other collections.
- R6. Read-only: colleague calendars never receive writes; they cannot be chosen for new events, and their events open view-only.
- R7. Lost access: if the colleague's calendar returns 403 or 404, its events are kept, the failure is logged (calendar id and status only), and it is retried next sync. It is not removed automatically.
- R8. Remove: a colleague calendar's sidebar menu has "Remove", behind a confirmation. It deletes the calendar and its events from this PC only.
- R9. Permission: the app requests `Calendars.Read.Shared` with its other scopes ([account-management](account-management.md) R2). The tenant admin adds it to the app registration (Microsoft Graph, Delegated) and grants admin consent, so existing accounts get the scope without a prompt. Without consent the account needs sign-in again (sync-ms365 R11).

## Out of scope

- Writing to colleague calendars (`Calendars.ReadWrite.Shared`).
- A colleague's custom (non-primary) calendars, and Microsoft 365 Group calendars.
- Reading the list of people shown in Outlook; Graph does not expose it.
- Calendars shared as free/busy only.

## Acceptance criteria

- [x] Adding a missing colleague's email shows their calendar in the sidebar, named after them, with their events after the next sync.
- [ ] A wrong email, or a colleague who has not shared, shows the message and adds nothing.
- [ ] Adding the same email twice, or the owner of an already listed calendar, is refused.
- [x] The colleague calendar survives repeated syncs and an app restart.
- [ ] A change to the colleague's event appears after the next sync (delta).
- [ ] The calendar is read-only: no drag, no edit, not offered for new events.
- [x] Remove deletes it after confirmation; the colleague's mailbox is unchanged.
- [x] With admin consent granted, the existing MS365 account keeps syncing without a sign-in prompt after the update.

## Known issues

- 2026-11-11: some 30-day chunks of four colleague calendars get HTTP 403 in the app (1 to 5 chunks each), although the same `calendarView/delta` range, with the text-body header and its deltaLink, returns 200 in Graph Explorer. Those chunks keep their events, start afresh every sync and log one warning per calendar. Accepted by the user; to diagnose later, log the failing step and Graph's error code.
- Not checked in the app at closure (user closed it): the wrong-email / free-busy messages, duplicate refusal, delta changes, and read-only behaviour. Unit tests cover the lookup, duplicates, delta pull and removal.

## Dependencies

- [sync-ms365](sync-ms365.md), [account-management](account-management.md), [calendar-sets](calendar-sets.md)

## Decisions

- 2026-11-11: confirmed in Graph Explorer that the missing calendars are absent from `/me/calendars` and from each calendar group, and that `/users/{email}/calendar` and `/users/{email}/calendarView/delta` return events with subjects under `Calendars.Read.Shared`.
- 2026-11-11: read-only, permission requested always with admin consent (user's choice).
- 2026-11-11: admin consent for `Calendars.Read.Shared` granted on the Plant Werx app registration.
- 2026-11-11: a colleague whose `/calendar` was readable but whose events returned 403 was added and stayed empty; R1 now also checks one day of events before adding (user's choice).
