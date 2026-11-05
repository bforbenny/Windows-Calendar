---
description: Technology stack, packaging, process model, tray icon and start-with-Windows behaviour
when-to-use: Before any project setup, build, packaging, or process/lifecycle work
tags: [platform, winui, msix, tray, startup]
status: in-progress
---

# App platform

## Summary

A native Windows 11 desktop app built with C# / .NET 10 and WinUI 3 (Windows App SDK 2.6.x stable). It is packaged as MSIX for personal sideload. A single process runs the UI and a tray-resident background mode so that sync and reminders keep running when the window is closed.

## Requirements

- R1. Language/runtime: C# on .NET 10 (LTS).
- R2. UI: WinUI 3 on the current stable Windows App SDK (2.6.x at time of writing), Fluent / Windows 11 styling. Mica backdrop on the title bar and sidebar only; the calendar grid, event cards, dialogs and flyouts use solid surfaces from [ui-colour-theme](ui-colour-theme.md) so contrast does not depend on the wallpaper. Layout follows [display-scaling](display-scaling.md).
- R2a. Theme: Light / Dark / Use Windows setting (default) in settings. "Use Windows setting" follows the Windows app mode and switches live without a restart; the tray flyout and toasts follow the same theme. Colours come from [ui-colour-theme](ui-colour-theme.md); the app's Navy accent overrides the Windows accent colour inside the app. High-contrast themes are honoured (palette replaced by system colours).
- R3. Packaging: MSIX, sideloaded with a self-signed certificate. Package identity is used for toasts and startup task. Updates and install steps follow [app-updates](app-updates.md).
- R4. Single-instance app. Launching again activates the existing instance.
- R5. Closing the main window hides it to the system tray; the process keeps running background sync and reminders. Tray right-click menu: Open, Sync now, Quit. Quit warns and asks first when unsynced changes or open conflicts exist ("3 changes have not synced. They sync next time the app runs, but are lost if the app is uninstalled."). Tray left-click opens the [tray-flyout](tray-flyout.md).
- R6. Optional "Start with Windows" (MSIX StartupTask), user-toggleable in settings, starts minimised to tray.
- R7. Key libraries: MSAL.NET (Microsoft sign-in with WAM broker), Microsoft Graph SDK or raw HTTP, Ical.Net 5.x (iCalendar parse/serialise, recurrence expansion), Microsoft.Data.Sqlite (local store).
- R8. Logging to a local rolling file under the app's LocalState folder. Logs must never contain credentials, tokens, or Authorization headers (CLAUDE.md security rules).
- R9. Errors on network/sync requests are logged and surfaced in the UI; no tight retry loops (exponential backoff only).

## Out of scope

- Microsoft Store distribution.
- Non-Windows platforms.
- Windows 10 support (target Windows 11 only).

## Acceptance criteria

- [ ] App builds and installs from MSIX on Windows 11.
- [ ] Closing the window leaves the tray icon; reminders still fire.
- [ ] Start-with-Windows toggle works across a reboot and starts in tray.
- [ ] Second launch focuses the existing window.
- [ ] Switching Windows between light and dark mode re-themes the open app immediately when the theme is "Use Windows setting".
- [ ] Grep of log files after a full sync shows no tokens or passwords.

## Dependencies

- None (foundation PRD).

## Open questions

- Any new NuGet packages require user approval at implementation time (CLAUDE.md agent rule).
