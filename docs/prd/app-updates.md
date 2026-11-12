---
description: Installing updates of the sideloaded MSIX, keeping data across versions, and setting up a new PC
when-to-use: Before working on packaging, versioning, schema migrations, or install instructions
tags: [platform, msix, updates, install]
status: closed
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

## Implementation (decided 2026-11-12)

- Release folder: `C:\Users\Ben\OneDrive\WindowsCalendar Releases`, holding `WindowsCalendar_<version>_x64.msix` for every build, `WindowsCalendar.appinstaller` (points at the latest build) and `WindowsCalendar.cer` (public certificate only). Old builds are kept. The `.appinstaller` stores absolute file URIs, so a new PC needs the same path (same Windows user name and OneDrive location).
- `tools/package/Publish-Msix.ps1` does a release: it stops if the app is running, adds 1 to the third part of the `Package.appxmanifest` version, finds or creates the signing certificate, builds a signed x64 MSIX (Release), and writes the three files above.
- Certificate: self-signed code-signing certificate, subject `CN=Ben` (the manifest Publisher), valid 5 years, private key non-exportable in `Cert:\CurrentUser\My` on the build PC. No password and no `.pfx` exist. Builds can only be signed on that PC; another PC only installs.
- GitHub release (added 2026-11-12): the script also commits and pushes the version bump and creates a public release `v<version>` on the GitHub repo with the `.msix`, the `.cer` and a second `.appinstaller` whose URIs point at GitHub (`releases/latest/download/WindowsCalendar.appinstaller` and the release's `.msix`). A PC installed from that file checks the latest GitHub release; one installed from the OneDrive file checks OneDrive. Release notes come from `tools/package/release-notes.md`. The GitHub CLI must be signed in; if the release step fails, the local release stays complete.
- Update check (R2): `OnLaunch`, `HoursBetweenUpdateChecks="24"`, `ShowPrompt="true"`, `UpdateBlocksActivation="false"`. The app lives in the tray and starts with Windows, so launches are rare; double-clicking the `.appinstaller` installs a new build at once (App Installer closes the running app; unsent changes are kept in the store).
- Downgrade (R4): a message box with the R4 text on start, then the app exits before the window, tray, sync and reminders start.
- Switch from the development registration: the installed app was registered from `bin` (development mode), which a signed package cannot update. LocalState is copied to a backup, the registration is removed with `Remove-AppxPackage -PreserveApplicationData`, and the signed package is installed from the `.appinstaller`.
- Visual Studio F5 deploys the same package identity and would replace the installed package (and its LocalState). Do not debug with F5 on a PC that has the signed package installed.

## Out of scope

- Microsoft Store distribution, an update server, or delta updates.
- Exporting or migrating local data and settings to another PC.

## Acceptance criteria

- [x] Installing a newer build over an older one keeps accounts, calendars, colours, sets and unsynced changes.
- [x] Launching after a new build is placed in the folder offers the update.
- [x] Running an older build against a newer store shows the downgrade message and leaves the data unchanged.
- [x] Following the README steps installs the app on a clean PC.

## Dependencies

- [app-platform](app-platform.md), [local-store-offline](local-store-offline.md), [settings-page](settings-page.md)

## Open questions

- Credential cleanup: verify during implementation whether PasswordVault entries outlive an uninstall, and document the manual cleanup if they do (CLAUDE.md security rule 3).
