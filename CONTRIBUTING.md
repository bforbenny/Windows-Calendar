# Contributing

Thanks for your interest in WindowsCalendar. This is a personal project, so contributions are welcome but may be declined if they do not fit the roadmap. Please open an issue before starting large changes.

## Requirements first

Every feature or behaviour change is backed by a PRD in [docs/prd/](docs/prd/), one requirement per file. The [README](README.md) has the index.

1. Find the PRD your change relates to. If none exists, open a feature request and propose one, starting from [_template.md](docs/prd/_template.md).
2. If your change alters a PRD's scope, update the PRD in the same pull request.
3. Reference the PRD (and requirement IDs such as `R3`) in your pull request.

PRD files are never moved or deleted; closure is a `status` change in the frontmatter (`pending -> in-progress -> closed`, or `closed -> updated -> in-progress -> closed`).

## Security rules (non-negotiable)

- Never hard-code credentials, tokens or keys. Do not commit `.env` files, certificates (`.pfx`) or secrets.
- Never log or print secrets, including Authorization headers and session tokens.
- Clear credentials from memory as soon as they are no longer needed.
- Never pass passwords as command-line arguments.

See [SECURITY.md](SECURITY.md) for reporting vulnerabilities.

## Development setup

- Windows 11
- .NET 10 SDK
- Visual Studio 2026 (or later) with the WinUI / Windows App SDK workload

Build and packaging details are in [app-platform](docs/prd/app-platform.md) and [app-updates](docs/prd/app-updates.md).

## Dependencies

Do not add new NuGet packages (or PowerShell modules / Python packages for tooling) without discussing them in an issue first.

## Code style

- Formatting follows [.editorconfig](.editorconfig). Run `dotnet format` before committing.
- Files are UTF-8. Avoid emoji in code, comments and docs.
- Keep changes focused: one concern per pull request, no unrelated refactors.

## Commits and pull requests

- Write commit subjects in the imperative mood, 72 characters or fewer (e.g. `Add week number column to month view`).
- Make sure the project builds and all tests pass.
- Fill in the pull request template, including the PRD reference and verification steps.

## Code of conduct

This project follows the [Code of Conduct](CODE_OF_CONDUCT.md).
