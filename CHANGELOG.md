# Changelog

All notable changes to unlose are documented here.
This project follows [Keep a Changelog](https://keepachangelog.com/) and uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

See [GitHub Releases](https://github.com/unlose-app/unlose/releases) for published versions.

## [1.0.9] - 2026-08-16

### Fixed

- Upgrade with the protection service running hit error 2803 again: the resident-process kill terminated UI/MCP only, while the running service image kept `unlose.Service.exe` locked at InstallValidate → the kill chain now stops `unloseService` via SCM first (`sc stop` → ~3 s wait → `taskkill` backstop, plain exe CAs); the new package's ServiceControl restarts the service after the upgrade
- Any residual file lock (live CLI session, antivirus) now degrades to a branded `MsiRMFilesInUse` / `FilesInUse` dialog (ListBox bound to `FileInUseProcess` per the InstallValidate contract, auto-close-and-restart checkbox) instead of the fatal 2803 error box

### Changed

- Install is now fully shell-free: every console-exe custom action (`sc stop`, `ping` wait, `taskkill`, `sc failure`, uninstall cleanup) was replaced by an in-process .NET Framework 4.8 DTF custom-action DLL calling the SCM/process APIs directly (`ServiceController` stop, `Process.Kill`, `ChangeServiceConfig2` failure policy) — the exe CAs flashed console windows on the desktop and the hide-target flag did not suppress them in host testing
- Progress dialog no longer looks frozen: an ActionText table feeds the step line (preparing / stopping service / copying / removing old version / starting service / finalizing, bilingual), and the progress bar + step line got the mandatory `Subscribe` EventMapping wiring (SetProgress/ActionText) without which MSI never updates them; the language + success dialogs also show on same-version double-click reinstalls (only ARP repair/uninstall stay quiet)
- The INST-001 tiered service-recovery policy (restart after 5 s / 5 s / 60 s, counter reset daily) is applied via `ChangeServiceConfig2` in the custom-action DLL; it opens the service with `SERVICE_ALL_ACCESS` and enables `SE_SHUTDOWN_NAME`, since the minimal `SERVICE_CHANGE_CONFIG` open was answered with `ERROR_ACCESS_DENIED` on this machine
- Progress bar no longer sits at 0% during the script/validate phase: an immediate custom action (`NudgeInitialProgress`, right after `InstallInitialize`) advances the bar ~5% via the documented ProgressAddition + ProgressReport message pair
- Every engine built-in step line on the install/upgrade path is overridden with the bilingual property pair — the zh-CN built-ins read e.g. "正在发布产品信息" for `RegisterProduct`, which the owner found misleading; `RegisterProduct`/`PublishFeatures`/`PublishProduct` now show "正在注册产品信息... / Registering product information...", and the removal/cleanup steps of the upgrade path got matching wording ("正在清理旧版本...", "正在移除旧文件...", "正在准备文件夹与快捷方式...", "正在更新注册表与环境...")

## [1.0.5] - 2026-08-15

First public release candidate.

### Added

- Self-contained single-file MSI (139.7 MB, .NET 8 runtime bundled) — clean Windows 10/11 machines install with no prerequisites
- Daily automatic update check: GETs a static `version.json` from unlose.app, carries no user or machine information; update buttons on the About page and home dashboard
- Installer language-selection dialog (zh/en, follows OS display language; admin override via `UILANG=en`); brand header with one-line intro
- Auto-launch tray UI after install; single neutral `unlose` shortcut name
- Snapshot dedup (server-enforced + `--skip-if-recent` escape hatch), explicit `--pin` for destructive-operation snapshots, `--quiet` flag
- Notification policy three-tier (all / failures-only / silent) with tray snooze
- Daily retention keeps earliest + latest snapshot per day

### Fixed

- MCP handshake blocked by synchronous VSS snapshot (30s timeout) → fire-and-forget, handshake ~209 ms
- Snapshot concurrency race (immediate mutex timeout → hard failure) → 15s lock wait + 60s merge window
- Four-segment version number broke MajorUpgrade (double installs) → three-segment MSI version + `AllowSameVersionUpgrades`
- Full-UI upgrade blocked by error 2803 (Restart Manager files-in-use dialog missing) → `KillResidentProcesses` before InstallValidate
- Start Menu/Desktop shortcut opened the install folder instead of the app → explicit shortcut `Target`
- Service Description in English; bilingual downgrade message

### Tested

- 134/134 unit tests, 16/16 UI automation tests (FlaUI)
- Windows 10 22H2 clean machine (VirtualBox, zero .NET): install → service → CLI → UI → snapshot → restore (SHA256 verified) → in-place upgrade → clean uninstall
- 30+ AI agents detected; 12-agent channel matrix verified

## [Internal development history]

### Added (since internal development)

- Full-disk VSS snapshots before AI agent sessions (30+ agents detected)
- Immersive dual-pane restore timeline with four-color and line-level diff
- Pick-and-choose file/directory restore to a new directory (never overwrites)
- Global memory injection into `~/AGENTS.md` and installed agents' memory files
- MCP server + CLI (`unlose.exe`) + skill file (`unlose-snapshot.skill.md`)
- 134 unit tests / 16 UI automation tests / VM end-to-end test suite
- Byte-identical SHA256-verified restore on real hosts

### Known limitations

- Windows-only (macOS on roadmap after Windows validation; Linux not planned)
- Current release is unsigned — verify SHA256 against each Release page

---

*Older internal versions predate public release and are not listed here.*
