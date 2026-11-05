---
description: Installing updates of the sideloaded MSIX, keeping data across versions, and setting up a new PC
when-to-use: Before working on packaging, versioning, schema migrations, or install instructions
tags: [platform, msix, updates, install]
status: pending
---

# App updates and install

## Summary

The sideloaded MSIX is updated through an App Installer (`.appinstaller`) file kept in a folder the user chooses, so new builds install with a double-click or are picked up at launch. App data in LocalState is kept across updates. A new PC needs the signing certificate trusted once.

## Requirements

- R1. Each build produces an MSIX with an incremented version and an `.appinstaller` file pointing to it. Both are placed in a user-chosen folder (local or synced, for example OneDrive). The app is installed from the `.appinstaller` file.
- R2. Update check: App Installer checks on launch (`OnLaunch`, at most every 24 hours) and shows its prompt; the update applies at the next start, never while the app is running.
- R3. Data kept: updates keep LocalState (SQLite store, outbox, conflicts, logs), LocalSettings and PasswordVault entries. Schema migrations are forward-only and run on the first start after an update ([local-store-offline](local-store-offline.md) R1).
- R4. Downgrade: if the store's schema is newer than the running build understands, the app does not open the store, changes nothing, and shows "This data was created by a newer version. Install the latest version."
- R5. Every build is signed with the same self-signed certificate ([app-platform](app-platform.md) R3). Install steps in the README cover trusting the certificate (Local Machine, Trusted People) and installing from the `.appinstaller` file, for first install and for a new PC.
- R6. Uninstall removes LocalState, including unsynced changes and conflict copies; nothing moves to a new PC except what is on the servers. This is stated in Settings > About ([settings-page](settings-page.md) R7), and Quit warns when unsynced changes or open conflicts exist ([app-platform](app-platform.md) R5).

## Out of scope

- Microsoft Store distribution, an update server, or delta updates.
- Exporting or migrating local data and settings to another PC.

## Acceptance criteria

- [ ] Installing a newer build over an older one keeps accounts, calendars, colours, sets and unsynced changes.
- [ ] Launching after a new build is placed in the folder offers the update.
- [ ] Running an older build against a newer store shows the downgrade message and leaves the data unchanged.
- [ ] Following the README steps installs the app on a clean PC.

## Dependencies

- [app-platform](app-platform.md), [local-store-offline](local-store-offline.md), [settings-page](settings-page.md)

## Open questions

- Credential cleanup: verify during implementation whether PasswordVault entries outlive an uninstall, and document the manual cleanup if they do (CLAUDE.md security rule 3).
