# Security Policy

unlose is a security tool: it runs as a LocalSystem Windows service, takes full-volume VSS snapshots, and can restore files. We take the security of the project and its users seriously.

## Reporting a vulnerability

**Please do NOT open public issues for security vulnerabilities.**

- **GitHub private advisory (preferred)**: use the "Report a vulnerability" button on the repository's Security tab — encrypted, tracked, and processed through GitHub's native flow
- **Email**: `security@unlose.dev` (for reporters who prefer email; forwarded to the maintainer)

**Response SLA**: we acknowledge receipt within **48 hours**, provide an initial
assessment within **7 days**, and aim to ship a fix or mitigation within **30 days**
(severity-dependent). If we cannot meet a milestone, we will tell you and give a
revised date.

Please include:

1. Product version (from `unlose --version` or the MSI build) and Windows version
2. Steps to reproduce (minimal, deterministic if possible)
3. Expected vs. actual behavior
4. Any relevant logs (`%ProgramData%\unlose\`, `%TEMP%\unlose-*.log`)

## Scope

In scope:

- Remote code execution or privilege escalation in the service, UI, CLI, or MCP server
- Path traversal / arbitrary file access via IPC commands (named pipe) or CLI
- Snapshot confidentiality or integrity failures
- Tampering with `%ProgramData%\unlose\` data (snapshot records, config)

Out of scope (known limitations, see README "We deliberately do NOT"):

- Command interception: unlose is a post-incident safety net, not a command interceptor. Destructive commands are not and will never be blocked.

## Handling

1. Acknowledgment within **48 hours**.
2. Initial assessment and fix plan within **7 days**; severity-dependent.
3. Fix or mitigation within **30 days** (or a communicated revised date).
4. Coordinated disclosure: we prefer 90 days from fix release before public disclosure, and we credit reporters (unless anonymity is requested).

## Security-relevant architecture notes (for auditors)

- VSS snapshots are created via WMI `Win32_ShadowCopy` and mounted via `mklink` symbolic links under `%ProgramData%\unlose\mounts\` (`.NET` enumeration and `robocopy` cannot access `GLOBALROOT` device paths directly).
- Restore requests are validated by the dispatcher: absolute paths and `..` segments are rejected.
- The named-pipe server is ACL-restricted (`System.IO.Pipes.AccessControl`).
- Snapshots are stored in Windows VSS shadow storage — they are not ordinary files and cannot be deleted by file-level operations (including AI agents and ransomware).
