---
description: Loading, empty, offline, error and first-launch states of the UI
when-to-use: Before working on sync status display, notice bars, empty views, search with no results, or the first-run experience
tags: [ui, states, errors, offline, onboarding]
status: closed
---

# UI states

## Summary

Views render instantly from the local store, so the UI has no loading spinners for calendar data. Problems (offline, sign-in needed, conflicts, sync errors) appear as notice bars with an icon and an action. Empty views show a short message, and the first launch shows a simple "Add account" panel.

## Requirements

- R1. Loading: views never show a spinner for calendar data; they render from the local store ([local-store-offline](local-store-offline.md)). Sync progress shows as the sync status in the Sync button's tooltip (e.g. "Sync now (F5) · Syncing…", "Sync now (F5) · Synced 10:42"), and per collection in the sidebar as a hue wave on the name of each calendar or task list while it syncs ([calendar-views](calendar-views.md) R2b).
- R1a. First sync: until an account's first sync completes, views show "Syncing <account> for the first time…" in place of the empty-state text (R4), so an empty grid is never presented as "Nothing scheduled".
- R2. Notice bars (WPF-UI `InfoBar`) float over the calendar ([floating-banners](floating-banners.md)), one per condition, each with an icon, a message and an action where one applies:

  | Condition | Severity / colour | Message (example) | Action |
  | --- | --- | --- | --- |
  | Offline (no server answers) | Warning (Amber) | "Offline. Changes are saved and will sync later. Last synced 10:42." | none |
  | Sign-in needed | Error (Red) | "Sign in to <account> to resume syncing." | Sign in |
  | Sync error | Error (Red) | "<account> could not sync: <short reason>. Last synced 10:42." | Retry |
  | Conflicts | Error (Red) | "2 changes conflicted with the server. Your versions were kept as copies." | Review |

  Bars clear automatically when the condition ends. Informational bars can be closed; error bars stay until resolved. Error text never includes credentials or tokens (CLAUDE.md security rules). While the window is hidden, a Sign-in needed condition also shows one toast ([sync-ms365](sync-ms365.md) R11).
- R3. Pending changes: while the outbox holds unsent changes, a count badge shows on the Sync button and the tooltip says "3 changes waiting"; clicking the badge opens the pending changes list ([local-store-offline](local-store-offline.md) R9a). The badge is its own tab stop with the screen-reader name "3 changes waiting, show list".
- R4. Empty states, as a single line of secondary text, centred:
  - Agenda or Day view with no events: "Nothing scheduled.";
  - search with no results: "No results." plus "Clear filters" when filters are active (the active calendar set counts as a filter, [search](search.md) R4);
  - task list with no tasks: "No tasks.".
- R5. First launch (no accounts): the main window shows an empty calendar with a centred panel "Add an account to get started" and two buttons, "Microsoft 365" and "Radicale" ([account-management](account-management.md)). No wizard; working hours, holiday calendars and theme are set later in Settings.
- R6. All notices and states follow [ui-colour-theme](ui-colour-theme.md) R4: colour is always paired with an icon and text.

## Build details

- Offline (R2): there is no network at all, or every account that tried to sync in the last run failed with a network error (no answer, timeout). A self-hosted server that answers on the LAN while Windows reports no internet is not offline. The bar clears after a run in which a server answered.
- Sign-in needed (R2): an account paused by a 401 or by MS365 needing interaction. Sign in opens the MS365 interactive sign-in, or for Radicale a masked password prompt (PasswordBox; the password is saved to Credential Manager and cleared from memory, CLAUDE.md security rules). The Radicale prompt checks the password with the server before saving it, and stays open with the reason when it is refused or the server cannot be reached. Success resumes the account and syncs. With several accounts the bar says "Sign in to 2 accounts" and Sign in goes through them in turn. The toast shows once per account per session.
- Sync error (R2): an account whose last run failed for another reason; the short reason is the error kind and HTTP status, never server text that could hold a token. Retry runs Sync now. With several accounts the bar names the count.
- One bar per condition, each covering all accounts with that condition. Error bars are not closable.
- Offline is noticed within seconds: Windows reporting a network change also starts a sync, whose results decide it.
- Sync error also covers unexpected failures inside a sync ("unexpected error, see the log"), so no failure is silent.
- First sync (R1a): an account is marked "synced once" in the store after its first successful run; an account that already has items counts as synced. The text replaces Agenda's "Nothing scheduled" and Tasks' "No tasks", and only while the account has no outcome yet: once its first run fails, the bar says why instead.
- Screen readers announce the bars when they open (error bars assertively) and the toolbar status when it moves between Offline, changes waiting and Synced; "Syncing…" and each new "Synced" time are not announced, so a sync every 5 minutes is quiet (live regions).
- Pending changes (R3): the sync status shows "n changes waiting" whenever the outbox is not empty, over "Synced 10:42"; "Syncing…" and "Offline" take precedence. The badge shows whenever the outbox is not empty, also while syncing or offline.
- The status text is not on screen; a hidden live region carries the announcements above, and the Sync button's screen-reader name is "Sync now, <status>". The list is [local-store-offline](local-store-offline.md) R9a.

## Build slices

Each slice is built, tested and checked in the app before the next starts; [display-scaling](display-scaling.md) and [ui-style](ui-style.md) follow as slices 2 and 3 of the same piece of work.

1. All of this PRD: R1 toolbar status, R1a, R2 bars and toast, R3 with the pending changes list (local-store-offline R9a, Retry and Discard). R4's "Clear filters" comes with the search filters themselves ([nl-search](nl-search.md), search R4); until then "No results" stands alone.

## Decisions

- 2026-11-12: the Conflicts bar uses Error (red) severity, per R2 (user's choice); it was Warning (amber) before.
- 2026-11-12: after the slice 1 check, the toolbar status text moved into the Sync button's tooltip, with a count badge on Sync for the pending changes list (user's choice).
- 2026-11-12: Discard is built in slice 1 (user's choice), as local-store-offline R7 describes: the local version goes to Conflicts and the server version is fetched again.

## Out of scope

- A setup wizard or product tour.
- Skeleton screens or progress bars for calendar data (the sidebar hue wave of R1 is the only per-collection progress).

## Acceptance criteria

- [ ] With the network off, an amber Offline bar appears and edits still save; it disappears after reconnecting and syncing.
- [ ] Revoking an MS365 sign-in shows a red bar with a working "Sign in" button.
- [ ] First launch with no accounts shows the "Add an account" panel.
- [ ] No spinner appears when switching views or periods.

## Dependencies

- [local-store-offline](local-store-offline.md), [account-management](account-management.md), [conflict-resolution](conflict-resolution.md), [search](search.md), [ui-colour-theme](ui-colour-theme.md), [ui-style](ui-style.md)
