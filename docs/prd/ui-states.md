---
description: Loading, empty, offline, error and first-launch states of the UI
when-to-use: Before working on sync status display, notice bars, empty views, search with no results, or the first-run experience
tags: [ui, states, errors, offline, onboarding]
status: pending
---

# UI states

## Summary

Views render instantly from the local store, so the UI has no loading spinners for calendar data. Problems (offline, sign-in needed, conflicts, sync errors) appear as notice bars with an icon and an action. Empty views show a short message, and the first launch shows a simple "Add account" panel.

## Requirements

- R1. Loading: views never show a spinner for calendar data; they render from the local store ([local-store-offline](local-store-offline.md)). Sync progress shows only as the toolbar sync status (icon + text, e.g. "Syncing…", "Synced 10:42").
- R1a. First sync: until an account's first sync completes, views show "Syncing <account> for the first time…" in place of the empty-state text (R4), so an empty grid is never presented as "Nothing scheduled".
- R2. Notice bars (WinUI `InfoBar`) appear under the toolbar, one per condition, each with an icon, a message and an action where one applies:

  | Condition | Severity / colour | Message (example) | Action |
  | --- | --- | --- | --- |
  | Offline (no server answers) | Warning (Amber) | "Offline. Changes are saved and will sync later. Last synced 10:42." | none |
  | Sign-in needed | Error (Red) | "Sign in to <account> to resume syncing." | Sign in |
  | Sync error | Error (Red) | "<account> could not sync: <short reason>. Last synced 10:42." | Retry |
  | Conflicts | Error (Red) | "2 changes conflicted with the server. Your versions were kept as copies." | Review |

  Bars clear automatically when the condition ends. Informational bars can be closed; error bars stay until resolved. Error text never includes credentials or tokens (CLAUDE.md security rules). While the window is hidden, a Sign-in needed condition also shows one toast ([sync-ms365](sync-ms365.md) R11).
- R3. Pending changes: while the outbox holds unsent changes, the toolbar sync status shows the count (e.g. "3 changes waiting"); clicking it opens the pending changes list ([local-store-offline](local-store-offline.md) R9a).
- R4. Empty states, as a single line of secondary text, centred:
  - Agenda or Day view with no events: "Nothing scheduled.";
  - search with no results: "No results." plus "Clear filters" when filters are active (the active calendar set counts as a filter, [search](search.md) R4);
  - task list with no tasks: "No tasks.".
- R5. First launch (no accounts): the main window shows an empty calendar with a centred panel "Add an account to get started" and two buttons, "Microsoft 365" and "Radicale" ([account-management](account-management.md)). No wizard; working hours, holiday calendars and theme are set later in Settings.
- R6. All notices and states follow [ui-colour-theme](ui-colour-theme.md) R4: colour is always paired with an icon and text.

## Out of scope

- A setup wizard or product tour.
- Skeleton screens or progress bars for calendar data.

## Acceptance criteria

- [ ] With the network off, an amber Offline bar appears and edits still save; it disappears after reconnecting and syncing.
- [ ] Revoking an MS365 sign-in shows a red bar with a working "Sign in" button.
- [ ] First launch with no accounts shows the "Add an account" panel.
- [ ] No spinner appears when switching views or periods.

## Dependencies

- [local-store-offline](local-store-offline.md), [account-management](account-management.md), [conflict-resolution](conflict-resolution.md), [search](search.md), [ui-colour-theme](ui-colour-theme.md), [ui-style](ui-style.md)
