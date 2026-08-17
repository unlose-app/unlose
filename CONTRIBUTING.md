# Contributing to unlose

Thanks for considering a contribution! unlose is an open-source (Apache-2.0) project by and for people who have been burned by AI agents. PRs, bug reports, docs, and thoughtful discussion are all welcome.

## Quick start for contributors

```powershell
# Requires: .NET 8 SDK, Windows 10/11 x64
git clone https://github.com/unlose-app/unlose.git
cd unlose
dotnet build src/Unlose.sln -c Release
dotnet test src/Unlose.Tests     # unit tests: fast, run everywhere
```

> **PR gate**: only the unit-test layer is required for external contributors. Integration/E2E tests (real VSS snapshots, VM round-trips) are run by maintainers before merge — the Pester scripts shipped in `tests/` cover install/restore smoke on a Windows machine.

## What we're looking for

- Bug reports with steps to reproduce (snapshot/restore is hard to test without a VM — describe the scenario precisely)
- Fixes and small features on the snapshot → mount → diff → restore chain
- New AI agent detection entries (`AgentRegistry`)
- Documentation, especially English copy
- Test coverage for edge cases (retention, path traversal, IPC contracts)

## What we deliberately don't do (don't PR these)

- ❌ Command interception / blocking — a losing cat-and-mouse game, deliberately out of scope (see README)
- ❌ Telemetry or any form of content collection — unlose is a time machine, not a monitor
- ❌ Fear-selling marketing copy — facts and real events only
- ❌ Linux support — **Windows-first by design**. unlose is built on Windows VSS, which has no Linux equivalent. macOS is on the roadmap after Windows product-market validation; Linux is not planned. Please don't open "port to Linux" issues or PRs — they will be closed as out of scope, not because the work isn't valuable, but because it would fragment a one-person project.

## Developer Certificate of Origin (DCO)

This project uses the **Developer Certificate of Origin** — by contributing, you certify that you have the right to submit the contribution under the project license (Apache-2.0).

Sign your commits:

```powershell
git commit -s -m "fix: ..."
# or retroactively:
git commit -s --amend
```

The DCO text: [https://developercertificate.org/](https://developercertificate.org/)

## Code style

- Match the surrounding code (C# / .NET 8, nullable enabled, implicit usings)
- Keep the `Unlose.*` namespace (historical; the external brand is `unlose`)
- No new dependencies without discussing in the PR (license compatibility matters)
- Comment in English or Chinese — both are welcome in this project

## Licensing

By contributing you agree that your contributions are licensed under Apache-2.0. The `unlose` name and logo are trademarks and are not granted by the code license — see NOTICE.
