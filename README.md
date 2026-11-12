# WindowsCalendar

Native Windows 11 calendar and tasks app with full offline read/write, syncing to Microsoft 365 (Microsoft Graph) and self-hosted Radicale (CalDAV). Personal use, distributed as a sideloaded MSIX.

## Install

The app is a sideloaded MSIX signed with a self-signed certificate ([app-updates](docs/prd/app-updates.md)). Each build is published to the [GitHub releases](https://github.com/bforbenny/Windows-Calendar/releases) and to `C:\Users\Ben\OneDrive\WindowsCalendar Releases`.

First install, or a new PC (Windows 11):

1. From the [latest release](https://github.com/bforbenny/Windows-Calendar/releases/latest), download `WindowsCalendar.cer` and `WindowsCalendar.appinstaller`.
2. Trust the certificate once, in an elevated PowerShell in the download folder:
   `Import-Certificate -FilePath .\WindowsCalendar.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople`
3. Open `WindowsCalendar.appinstaller` and choose Install. The app then checks the latest GitHub release for updates when it starts, at most every 24 hours, and asks before updating.
4. Sign in to the accounts again. Nothing local moves to a new PC; only what is on the servers comes back.

The app checks for updates where it was installed from: the GitHub `.appinstaller` checks GitHub, and the OneDrive `.appinstaller` checks the OneDrive folder.

## Publish a new build

`tools\package\Publish-Msix.ps1` builds, signs and publishes a release. Run it only on the build PC: it holds the signing key.

Before running:

- Quit the app from the tray. The script stops if the app is running.
- The GitHub CLI must be signed in (`gh auth login`). The script checks this before changing anything.
- Commit your work first. The script commits only the version bump, but its push also sends any other unpushed commits on the branch.

Run `powershell -ExecutionPolicy Bypass -File tools\package\Publish-Msix.ps1`, or the VS Code task "publish msix". In order, it:

1. Adds 1 to the third part of the version in `WindowsCalendar\Package.appxmanifest` (for example 1.0.41.0 to 1.0.42.0).
2. Finds the `CN=Ben` code-signing certificate in `Cert:\CurrentUser\My`, or creates one (5 years, private key not exportable, no password). A new certificate has to be trusted again on every PC.
3. Builds a signed x64 MSIX in Release.
4. Writes `WindowsCalendar_<version>_x64.msix`, `WindowsCalendar.appinstaller` and `WindowsCalendar.cer` to the OneDrive release folder (another folder with `-ReleaseDir`). Old builds are kept.
5. Commits `Package $version` (the manifest only) and pushes.
6. Creates the public GitHub release `v<version>` with the `.msix`, the `.cer` and a GitHub `.appinstaller`. The release notes come from `tools\package\release-notes.md`.

If the build fails, the version bump is undone and nothing is published. If the commit, push or release fails, the OneDrive release is complete; finish the GitHub part by hand.

To update this PC straight away, stop the app and install the new `.appinstaller`. Otherwise start the app from Start and accept the update prompt. `Add-AppxPackage -AppInstallerFile` does not accept `-ForceApplicationShutdown`, so stop the app first.

## Uninstall and debugging

Uninstalling deletes local data, including unsynced changes and conflict copies (Settings > About). Saved Radicale passwords may stay in Credential Manager > Web Credentials after an uninstall; remove them there by hand.

Do not press F5 in Visual Studio, or run `Add-AppxPackage -Register` from `bin`, on a PC with the signed package installed: both replace the installed package, with its local data.

## PRD index

Requirements live in [docs/prd/](docs/prd/), one requirement per file. Start a new one from [_template.md](docs/prd/_template.md).

| PRD | Description | Status |
| --- | --- | --- |
| [app-platform](docs/prd/app-platform.md) | Tech stack, packaging, tray, start with Windows | closed |
| [account-management](docs/prd/account-management.md) | Multiple MS365 and Radicale accounts, credential storage | closed |
| [local-store-offline](docs/prd/local-store-offline.md) | SQLite cache, outbox, offline read/write | closed |
| [sync-ms365](docs/prd/sync-ms365.md) | Graph calendar and To Do sync | closed |
| [ms365-colleague-calendars](docs/prd/ms365-colleague-calendars.md) | Colleague MS365 calendars added by email | closed |
| [sync-radicale](docs/prd/sync-radicale.md) | CalDAV sync for events and tasks | closed |
| [conflict-resolution](docs/prd/conflict-resolution.md) | Server wins, local copy preserved | closed |
| [recurring-events](docs/prd/recurring-events.md) | Recurrence model and edit scopes incl. "this and following" | closed |
| [calendar-views](docs/prd/calendar-views.md) | Week/Month views with natural scrolling, calendar colours, drag to move/resize | closed |
| [agenda-view](docs/prd/agenda-view.md) | Agenda: day columns of lists, free scrolling | closed |
| [attendees-invitations](docs/prd/attendees-invitations.md) | Meetings view-only with Open in Outlook; declined and cancelled meetings faded; needs-response text | closed |
| [event-show-as](docs/prd/event-show-as.md) | Busy / Free / Tentative / Out of office | closed |
| [online-meeting-join](docs/prd/online-meeting-join.md) | Join button for Teams/Zoom/Meet/Webex links | closed |
| [quick-add](docs/prd/quick-add.md) | Typed quick add, offline rule-based parser | closed |
| [duplicate-event](docs/prd/duplicate-event.md) | Duplicate an event | closed |
| [undo](docs/prd/undo.md) | Undo recent changes | closed |
| [keyboard-navigation](docs/prd/keyboard-navigation.md) | Full keyboard operation and shortcuts | closed |
| [calendar-sets](docs/prd/calendar-sets.md) | Named sets of visible calendars | closed |
| [calendar-shading](docs/prd/calendar-shading.md) | Working hours, public holidays, week numbers | closed |
| [app-updates](docs/prd/app-updates.md) | MSIX updates via App Installer, data across versions, new-PC install, Check for updates | in-progress |
| [tray-flyout](docs/prd/tray-flyout.md) | Tray pop-up with today's agenda and Join | closed |
| [ui-colour-theme](docs/prd/ui-colour-theme.md) | Palette, colour roles, calendar colours and shape markers | closed |
| [display-scaling](docs/prd/display-scaling.md) | Layout at 100% / 150% scaling, multi-monitor | closed |
| [ui-style](docs/prd/ui-style.md) | Font, type ramp, spacing, motion, locale formats | closed |
| [ui-states](docs/prd/ui-states.md) | Loading, empty, offline, error and first-launch states | closed |
| [floating-banners](docs/prd/floating-banners.md) | Banners float over the calendar and never push the layout | closed |
| [settings-page](docs/prd/settings-page.md) | Settings page layout and contents | closed |
| [tasks](docs/prd/tasks.md) | VTODO and Microsoft To Do tasks | closed |
| [event-location](docs/prd/event-location.md) | Plain-text location/address on events | closed |
| [reminders-notifications](docs/prd/reminders-notifications.md) | Windows toast reminders | closed |
| [search](docs/prd/search.md) | Full-text search over cached items | closed |
| [on-device-model](docs/prd/on-device-model.md) | Shared NPU language model service, AI setting, eval harness, spike gate | parked |
| [nl-quick-add](docs/prd/nl-quick-add.md) | NPU model refines quick add for free English phrasing | parked |
| [nl-search](docs/prd/nl-search.md) | Split into search-attendees, search-filters, search-date-chips | parked |
| [search-attendees](docs/prd/search-attendees.md) | Attendee and organiser names indexed for search | closed |
| [search-filters](docs/prd/search-filters.md) | Filter chips and Filters flyout for search (search R4) | closed |
| [search-date-chips](docs/prd/search-date-chips.md) | Date phrases in search become filter chips | closed |
| [timezone-handling](docs/prd/timezone-handling.md) | Correct TZ handling, per-event TZ, secondary TZ column | closed |
| [schedule-gap-warning](docs/prd/schedule-gap-warning.md) | Overlap and minimum-gap warning on save, drag and resize | closed |
