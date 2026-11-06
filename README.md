# WindowsCalendar

Native Windows 11 calendar and tasks app with full offline read/write, syncing to Microsoft 365 (Microsoft Graph) and self-hosted Radicale (CalDAV). Personal use, distributed as a sideloaded MSIX.

## PRD index

Requirements live in [docs/prd/](docs/prd/), one requirement per file. Start a new one from [_template.md](docs/prd/_template.md).

| PRD | Description | Status |
| --- | --- | --- |
| [app-platform](docs/prd/app-platform.md) | Tech stack, packaging, tray, start with Windows | in-progress |
| [account-management](docs/prd/account-management.md) | Multiple MS365 and Radicale accounts, credential storage | in-progress |
| [local-store-offline](docs/prd/local-store-offline.md) | SQLite cache, outbox, offline read/write | closed |
| [sync-ms365](docs/prd/sync-ms365.md) | Graph calendar and To Do sync | in-progress |
| [sync-radicale](docs/prd/sync-radicale.md) | CalDAV sync for events and tasks | in-progress |
| [conflict-resolution](docs/prd/conflict-resolution.md) | Server wins, local copy preserved | in-progress |
| [recurring-events](docs/prd/recurring-events.md) | Recurrence model and edit scopes incl. "this and following" | in-progress |
| [calendar-views](docs/prd/calendar-views.md) | Week/Month views with natural scrolling, calendar colours, drag to move/resize | in-progress |
| [agenda-view](docs/prd/agenda-view.md) | Agenda: day columns of lists, free scrolling | in-progress |
| [attendees-invitations](docs/prd/attendees-invitations.md) | Read-only attendees, needs-response marker, Open in Outlook, organiser guards | in-progress |
| [event-show-as](docs/prd/event-show-as.md) | Busy / Free / Tentative / Out of office | in-progress |
| [online-meeting-join](docs/prd/online-meeting-join.md) | Join button for Teams/Zoom/Meet/Webex links | pending |
| [quick-add](docs/prd/quick-add.md) | Typed quick add, offline rule-based parser | pending |
| [duplicate-event](docs/prd/duplicate-event.md) | Duplicate an event | in-progress |
| [undo](docs/prd/undo.md) | Undo recent changes | in-progress |
| [keyboard-navigation](docs/prd/keyboard-navigation.md) | Full keyboard operation and shortcuts | in-progress |
| [calendar-sets](docs/prd/calendar-sets.md) | Named sets of visible calendars | pending |
| [calendar-shading](docs/prd/calendar-shading.md) | Working hours, public holidays, week numbers | closed |
| [app-updates](docs/prd/app-updates.md) | MSIX updates via App Installer, data across versions, new-PC install | pending |
| [tray-flyout](docs/prd/tray-flyout.md) | Tray pop-up with today's agenda and Join | pending |
| [ui-colour-theme](docs/prd/ui-colour-theme.md) | Palette, colour roles, calendar colours and shape markers | in-progress |
| [display-scaling](docs/prd/display-scaling.md) | Layout at 100% / 150% scaling, multi-monitor | pending |
| [ui-style](docs/prd/ui-style.md) | Font, type ramp, spacing, motion, locale formats | pending |
| [ui-states](docs/prd/ui-states.md) | Loading, empty, offline, error and first-launch states | pending |
| [floating-banners](docs/prd/floating-banners.md) | Banners float over the calendar and never push the layout | closed |
| [settings-page](docs/prd/settings-page.md) | Settings page layout and contents | in-progress |
| [tasks](docs/prd/tasks.md) | VTODO and Microsoft To Do tasks | closed |
| [event-location](docs/prd/event-location.md) | Plain-text location/address on events | in-progress |
| [reminders-notifications](docs/prd/reminders-notifications.md) | Windows toast reminders | pending |
| [search](docs/prd/search.md) | Full-text search over cached items | pending |
| [timezone-handling](docs/prd/timezone-handling.md) | Correct TZ handling, per-event TZ, secondary TZ column | pending |
