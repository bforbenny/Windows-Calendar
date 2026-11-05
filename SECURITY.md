# Security Policy

## Supported versions

Only the latest released version receives security fixes.

## Reporting a vulnerability

Please do not report security issues in public issues, discussions or pull requests.

Report privately using GitHub's **Report a vulnerability** button on the repository's **Security** tab (private vulnerability reporting). Include:

- A description of the issue and its impact
- Steps to reproduce, or a proof of concept
- The affected version and Windows build

Do not include real credentials, tokens or personal calendar data in your report.

You should get an acknowledgement within 7 days. As this is a personal project, fix timelines are best effort.

## Scope

Of particular interest:

- Exposure of credentials, OAuth tokens or CalDAV passwords (in logs, files, memory dumps or process arguments)
- Bypass of Windows credential storage
- Parsing issues in iCalendar or Graph responses that lead to code execution or data corruption
- Weaknesses in MSIX packaging or update delivery
