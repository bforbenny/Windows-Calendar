---
description: Adding, editing and removing multiple MS365 and Radicale accounts, and how their credentials are stored
when-to-use: Before working on sign-in, account settings, credential storage, or calendar/task-list selection
tags: [accounts, auth, credentials, security, msal, radicale]
status: closed
---

# Account management

## Summary

The user can connect any number of Microsoft 365 work accounts and any number of Radicale servers. Each account exposes its calendars and task lists, and the user can show or hide and colour each one. Items stay in their source account; there is no automatic copying or mirroring between accounts. A user-initiated move or duplicate to another account is allowed ([calendar-views](calendar-views.md) R8, [duplicate-event](duplicate-event.md) R6).

## Requirements

- R1. Generic account model: `Account { id, type: MS365 | CalDAV, displayName, ... }` → `Collection { id, accountId, kind: Calendar | TaskList, name, colour, shape, remindersOn, readOnly }`. Visibility is not stored per collection; it comes from the active calendar set and sidebar overrides ([calendar-sets](calendar-sets.md) R5). A CalDAV collection that holds both VEVENT and VTODO becomes two logical collections (one Calendar, one TaskList) sharing one URL.
- R2. MS365 sign-in via MSAL.NET public client using the Windows broker (WAM), delegated permissions only: `Calendars.ReadWrite`, `Tasks.ReadWrite`, `User.Read`, `offline_access`.
- R3. MS365 app registration is single-tenant, in the user's own Entra ID tenant (user is tenant admin and grants consent). The client ID and tenant ID are build-time constants in the app's configuration, not secrets and not entered by the user; no client secret is used. Every MS365 account must belong to this tenant.
- R4. Radicale account: server URL (HTTPS required), username, password. Calendars and task lists discovered via CalDAV (current-user-principal → calendar-home-set → PROPFIND collections, reading `supported-calendar-component-set`).
  - Accounts store `server_url`, `username` and `email` (schema v3); none are secrets.
- R4a. Adding a Radicale account tests the connection before saving. Accepted URL forms: the server root (tries `/.well-known/caldav`, then `/`) or a principal URL. Each failure has its own message: not HTTPS; server unreachable; TLS certificate not trusted ("Install the server's certificate in Windows, then retry"); wrong username or password (401); no calendars found. The Radicale account also has an optional "Email address", used to tell meetings you organise from ones you attend ([attendees-invitations](attendees-invitations.md) R2).
  - `CalDavDiscovery` in `WindowsCalendar.Core`. A server root tries `/.well-known/caldav`, then `/`. Only PROPFIND is sent, so discovery never changes the server. Each failure maps to a fixed message; only status codes are logged.
- R5. Reject non-HTTPS Radicale URLs. Certificate validation stays on (no "ignore TLS errors" option in v1).
- R6. **Credential storage (approved exception to the CLAUDE.md "never stored" rule):** secrets are persisted only in OS-protected storage tied to the Windows login:
  - Radicale password → Windows Credential Manager (PasswordVault).
  - MSAL token cache → MSAL's DPAPI-protected cache / WAM broker.
  - Never in SQLite, config files, logs, or source.
  - PasswordVault entry: resource `WindowsCalendar/<accountId>`, user name = the server username.
- R7. Password entry uses a masked field. The in-memory plaintext copy is cleared after it is stored or used.
  - The WPF `PasswordBox` gives the value as a SecureString (CLAUDE.md rule 4). It is converted to plain text only for the connection test and the vault save, the plain copy is dropped and the box cleared at once. It never appears in logs or exception messages.
- R8. Removing an account deletes its credentials and all its cached data, after explicit confirmation (destructive action).
  - Remove is on the account's sidebar menu, behind a confirmation dialog. It deletes the vault entry, the account and its cached data, including its outbox entries.
- R9. Per-collection settings: colour, shape, reminders on ([reminders-notifications](reminders-notifications.md) R5), default calendar for new events, default task list for new tasks. When the first account is added, its main calendar (Graph `isDefaultCalendar`; CalDAV the first writable VEVENT collection) and first task list become the defaults. Read-only collections cannot be defaults. If a default is removed, the next writable collection in sidebar order takes over.
  - Defaults are kept in the store's `meta` table. New collections get colour and shape per [ui-colour-theme](ui-colour-theme.md) R8; snapping server colours (R9 there) comes later.
- R10. Account status indicator: OK / offline / auth required / error. Re-auth flow when tokens expire or the password changes. Each account shows its last successful sync time.
- R11. Re-discovery: the collection list is re-read on every sync (Graph `/me/calendars` and `/me/todo/lists`; CalDAV `PROPFIND Depth:1`). New collections get an automatic colour and shape and join only the "All" set. Renames update the name. A collection deleted on the server is removed locally; its pending outbox entries are moved to Conflicts first, so nothing is lost ([conflict-resolution](conflict-resolution.md) R9).
- R12. Editing an account: display name, the Radicale email address (R4a), and the password (through sign in again). Changing a Radicale URL or username means removing and re-adding the account.

## Interim (first slice: Radicale)

- The first-launch panel ([ui-states](ui-states.md) R5) offers Microsoft 365 and Radicale; the sidebar's Add account link offers both too.
- The sidebar lists accounts and their collections.
- Account status (R10) and account editing (R12) come with [sync-radicale](sync-radicale.md) and [settings-page](settings-page.md).
- MS365 (with [sync-ms365](sync-ms365.md) slice 1): R2 sign-in through MSAL with the WAM broker, from the first-launch panel and the sidebar's add-account menu. The client ID and tenant ID (R3) are read from `ms365.json` shipped with the app; the user fills them in before building. The account stores its display name and email (`/me` `mail`, else `userPrincipalName`) and the sign-in name in `username`, used to find the account in the broker for silent sign-in. Tokens stay in WAM (R6). Removing an MS365 account also signs it out of the app (MSAL `RemoveAsync`).
- MS365 after a restart: the broker lists the accounts Windows knows (`ListOperatingSystemAccounts`), so silent sign-in finds the account without the app storing tokens. If an account still needs to sign in, Add account with the same account signs it in again and resumes its sync instead of adding a duplicate.
- R8 removal during a running sync: the sync of that account stops writing once the account is gone. Its failure is logged as "removed during sync" (information, no details), not as a sync error, and the other accounts' results and the view refresh still apply.
- R8 remove then re-add: see [calendar-sets](calendar-sets.md) R8 for what is remembered and restored.
- R11 is in place for Radicale: the collection list is re-read at the start of every sync (Sync now included), matched by URL and kind. A listing with no collections at all removes nothing, so a server hiccup cannot wipe the account.

## Out of scope

- Personal Microsoft accounts (MSA), MS365 accounts in other tenants, and Google accounts.
- Automatic copying or mirroring of items between accounts.
- Creating or deleting calendars on the server (v1 uses existing collections only).
- Shared/delegated MS365 calendars of other users, other than those listed by `/me/calendars` and colleague primary calendars added by email ([ms365-colleague-calendars](ms365-colleague-calendars.md)).

## Acceptance criteria

- [x] Two MS365 accounts (same tenant) and two Radicale accounts can be added at the same time.
- [x] A calendar created on the server after setup appears in the sidebar after the next sync.
- [x] Adding a Radicale account with a wrong password or an untrusted certificate shows the specific message.
- [x] After a reboot, sync resumes without prompting (credentials taken from OS storage).
- [x] No password or token appears in the SQLite file, settings file, or logs.
- [x] Removing an account asks for confirmation and removes its vault entries and cached items.
- [x] Removing an account while it is syncing logs "removed during sync" and no "Sync run failed" error; other accounts still sync.

## Dependencies

- [app-platform](app-platform.md)
