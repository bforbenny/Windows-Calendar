# WindowsCalendar

Native Windows 11 calendar and tasks app with full offline read/write, syncing to Microsoft 365 (Microsoft Graph) and self-hosted Radicale (CalDAV). Personal use, distributed as a sideloaded MSIX.

## PRD index

Requirements live in [docs/prd/](docs/prd/), one requirement per file. Start a new one from [_template.md](docs/prd/_template.md).

| PRD | Description | Status |
| --- | --- | --- |
| [app-platform](docs/prd/app-platform.md) | Tech stack, packaging, tray, start with Windows | in-progress |
| [account-management](docs/prd/account-management.md) | Multiple MS365 and Radicale accounts, credential storage | pending |
| [local-store-offline](docs/prd/local-store-offline.md) | SQLite cache, outbox, offline read/write | pending |
| [sync-ms365](docs/prd/sync-ms365.md) | Graph calendar and To Do sync | pending |
| [sync-radicale](docs/prd/sync-radicale.md) | CalDAV sync for events and tasks | pending |
| [conflict-resolution](docs/prd/conflict-resolution.md) | Server wins, local copy preserved | pending |
| [recurring-events](docs/prd/recurring-events.md) | Recurrence model and edit scopes incl. "this and following" | pending |
| [calendar-views](docs/prd/calendar-views.md) | Day/Week/Month views, calendar colours, drag to move/resize | pending |
| [agenda-view](docs/prd/agenda-view.md) | Agenda/list view grouped by day | pending |
| [attendees-invitations](docs/prd/attendees-invitations.md) | Read-only attendees, needs-response marker, Open in Outlook, organiser guards | pending |
| [event-show-as](docs/prd/event-show-as.md) | Busy / Free / Tentative / Out of office | pending |
| [online-meeting-join](docs/prd/online-meeting-join.md) | Join button for Teams/Zoom/Meet/Webex links | pending |
| [quick-add](docs/prd/quick-add.md) | Typed quick add, offline rule-based parser | pending |
| [duplicate-event](docs/prd/duplicate-event.md) | Duplicate an event | pending |
| [undo](docs/prd/undo.md) | Undo recent changes | pending |
| [keyboard-navigation](docs/prd/keyboard-navigation.md) | Full keyboard operation and shortcuts | pending |
| [calendar-sets](docs/prd/calendar-sets.md) | Named sets of visible calendars | pending |
| [calendar-shading](docs/prd/calendar-shading.md) | Working hours, public holidays, week numbers | pending |
| [app-updates](docs/prd/app-updates.md) | MSIX updates via App Installer, data across versions, new-PC install | pending |
| [tray-flyout](docs/prd/tray-flyout.md) | Tray pop-up with today's agenda and Join | pending |
| [ui-colour-theme](docs/prd/ui-colour-theme.md) | Palette, colour roles, calendar colours and shape markers | pending |
| [display-scaling](docs/prd/display-scaling.md) | Layout at 100% / 150% scaling, multi-monitor | pending |
| [ui-style](docs/prd/ui-style.md) | Font, type ramp, spacing, motion, locale formats | pending |
| [ui-states](docs/prd/ui-states.md) | Loading, empty, offline, error and first-launch states | pending |
| [settings-page](docs/prd/settings-page.md) | Settings page layout and contents | pending |
| [tasks](docs/prd/tasks.md) | VTODO and Microsoft To Do tasks | pending |
| [event-location](docs/prd/event-location.md) | Plain-text location/address on events | pending |
| [reminders-notifications](docs/prd/reminders-notifications.md) | Windows toast reminders | pending |
| [search](docs/prd/search.md) | Full-text search over cached items | pending |
| [timezone-handling](docs/prd/timezone-handling.md) | Correct TZ handling, per-event TZ, secondary TZ column | pending |
