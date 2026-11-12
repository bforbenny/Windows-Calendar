WindowsCalendar {VERSION}: Windows 11 calendar and tasks app syncing Microsoft 365 and Radicale (CalDAV). x64, sideloaded MSIX signed with a self-signed certificate.

## Install

1. Download `WindowsCalendar.cer` and trust it once, in an elevated PowerShell in the download folder:
   `Import-Certificate -FilePath .\WindowsCalendar.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople`
2. Download and open `WindowsCalendar.appinstaller`. It installs the `.msix` from this release and checks this repo's latest release for updates at most every 24 hours, when the app is started. Or open `WindowsCalendar_{VERSION}_x64.msix` directly, without update checks.

Only trust the certificate if you trust this repo's owner: it lets any package signed with it install on your PC.

Uninstalling deletes local data, including unsynced changes. See the README for details.
