---
phase: 07-reports-deployment
plan: 03
subsystem: deployment
tags: [sqlite, backup, logging, windows-service, powershell]
requires: [07-01]
provides:
  - CafeDataPaths/DataLocation (ProgramData layout, connection-string precedence)
  - Hosting/FileLoggerProvider (daily files, 14-day retention)
  - BackupService + BackupOptions + BackupPanel (nightly backup, Back up now)
  - deploy/windows/install-server.ps1 and uninstall-server.ps1
affects: [07-04, 07-05]
tech-stack:
  added: []
  patterns: [pure path resolver, hand-written ILoggerProvider, singleton BackgroundService reused by UI]
key-files:
  created:
    - src/Cafe.Server/Hosting/CafeDataPaths.cs
    - src/Cafe.Server/Hosting/FileLoggerProvider.cs
    - src/Cafe.Server/Backups/BackupOptions.cs
    - src/Cafe.Server/Backups/BackupService.cs
    - src/Cafe.Server/Components/Shared/BackupPanel.razor
    - deploy/windows/install-server.ps1
    - deploy/windows/uninstall-server.ps1
    - tests/Cafe.Tests/CafeDataPathsTests.cs
    - tests/Cafe.Tests/FileLoggerProviderTests.cs
    - tests/Cafe.Tests/BackupServiceTests.cs
  modified:
    - src/Cafe.Server/Program.cs
    - src/Cafe.Server/appsettings.json
    - src/Cafe.Server/Components/Pages/Settings.razor
    - tests/Cafe.Tests/Infrastructure/CafeWebApplicationFactory.cs
    - .gitignore
key-decisions:
  - "Explicit ConnectionStrings:Cafe always wins; appsettings no longer ships a default so Production/service gets %ProgramData%\\CafeManager"
  - "install-server.ps1 updates an existing service via the registry ImagePath instead of sc.exe config binPath= (avoids PowerShell 5.1 native-argument quoting of paths with spaces)"
  - "Backups are named in cafe local time (Cafe:TimeZone) and copied via a temp file then moved, so a crash never leaves a partial backup"
status: complete
commits: 4
plan_head_before: b4304c55e2bd43a67f68aafb7e9f5411a9819bdc
plan_head_after: 173bfdd5e345ff66e6aa4c8c0747988735436cae
actuals:
  tokens: 60000
  tasks: 3
  commits: 4
duration: ~35 min
completed: 2026-11-20
---

# Phase 7 Plan 03: Server deployment Summary

ProgramData data layout with explicit-connection-string precedence, a hand-written daily file logger, nightly SQLite backups with a Settings "Back up now" panel, and PowerShell 5.1 install/uninstall scripts for the CafeManager Windows service.

## What was built

- **CafeDataPaths.Resolve** (pure): explicit connection string wins (also in Production/service); otherwise Production or a Windows service uses `<CommonApplicationData>\CafeManager\cafe.db` with `logs` and `backups` beside it; Development keeps `cafe.db` in the content root. `Cafe:DataDirectory` replaces the ProgramData root. Whitespace counts as unset. Program.cs registers the resulting `DataLocation` as a singleton, creates the folder before migration and logs only the database file path.
- **FileLoggerProvider**: `cafe-yyyyMMdd.log`, line format `2026-11-20 14:03:07.123 [Information] Category: message`, thread-safe, never throws, deletes files older than 14 days (day 14 is kept) on start and at day rollover, folds newlines onto space-prefixed continuation lines (T-07-08). Added only in the production layout.
- **BackupService**: `BackupNowAsync` (BackupDatabase to temp file, atomic move, prune to `Backup:Keep`, never throws to the UI), `LastBackupUtc`, `NextRunUtc` (DST aware, gap time moves to first valid time), scheduler loop gated by `Backup:RunScheduler`, serialised with a semaphore. `BackupPanel` on Settings shows last backup, folder, retention and the button.
- **Scripts**: install-server.ps1 (self-elevation, robocopy /MIR, SID-based ACL on ProgramData, delayed-auto service, failure recovery, firewall rule for Private/Domain, health poll, LAN URLs, opens localhost) and uninstall-server.ps1 (keeps data unless `-RemoveData`, typed DELETE unless `-Force`).

## Verification

- `dotnet build Cafe.sln`: 0 errors. `dotnet test tests/Cafe.Tests`: 1011 passed, 0 failed.
- Scripts: ASCII-only (grep clean), no `&&`, `??`, `?:`; `pwsh` is not installed on this host so they were not parsed. Reviewed by hand.

## Deviations from Plan

**1. [Rule 3 - Blocking] .gitignore `backups/` swallowed `src/Cafe.Server/Backups`**
- **Found during:** Task 2 commit (macOS file system is case-insensitive).
- **Fix:** added `!/src/Cafe.Server/Backups/` plus ignores for `*.db`/`*.tmp` in that folder. Sources were committed in a follow-up commit, so the Task 2 commit alone does not build (BackupService missing); the branch head builds and tests green.
- **Commit:** b01431a

**2. [Rule 1 - Bug] Script-level `$PSBoundParameters` inside functions**
- Elevation re-launch would have lost parameters; captured as `$ScriptArguments` at script scope before the functions use it.

**3. Service update via registry ImagePath** instead of `sc.exe config binPath=` (see key decisions). New installs use `New-Service`.

## Human checks (not run: requires Windows 10/11)

- [ ] Run `install-server.ps1`: UAC prompt, service "Cafe Manager Server" Running and Automatic (Delayed Start), `http://localhost:5080` opens setup, another LAN device reaches `http://<ip>:5080/login`.
- [ ] Kill the service process and confirm it restarts (recovery actions).
- [ ] Re-run the script and confirm it upgrades without errors or duplicate rule.
- [ ] Run `uninstall-server.ps1`: service and firewall rule gone, `C:\ProgramData\CafeManager` remains; `-RemoveData` asks for DELETE.
- [ ] Confirm Production run writes `%ProgramData%\CafeManager\logs\cafe-*.log` and a backup appears after "Back up now".

DEPL-01 is code-complete; it stays unchecked in REQUIREMENTS.md until the Windows run above is confirmed and the install docs (a later plan) exist.

## Known Stubs

None.

## Threat Flags

None beyond the plan's threat model (T-07-06 ACL, T-07-07 precedence, T-07-08 newline folding, T-07-10 firewall profiles, T-07-11 retention are all implemented).

## Self-Check: PASSED

Files and commits (5659e21, 0871a8e, b01431a, 173bfdd) verified present.
