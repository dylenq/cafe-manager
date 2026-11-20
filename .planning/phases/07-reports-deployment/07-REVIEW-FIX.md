---
phase: 07-reports-deployment
fixed_at: 2026-11-20T23:10:00Z
review_path: .planning/phases/07-reports-deployment/07-REVIEW.md
iteration: 1
findings_in_scope: 12
fixed: 12
skipped: 0
status: all_fixed
---

# Phase 7: Code Review Fix Report

**Fixed at:** 2026-11-20
**Source review:** .planning/phases/07-reports-deployment/07-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 12 (WR-01..WR-09 plus IN-04, IN-06, IN-07 as directed; the other Info findings were out of scope)
- Fixed: 12
- Skipped: 0

**Verification environment:** main checkout on macOS (`use_worktrees` was not used because the task required building and releasing from the main tree and nothing else was running). C# fixes were verified with `dotnet build` and tests. PowerShell was NOT executed: `pwsh` is not installed, so the PowerShell fixes were checked by review and grep only (ASCII only, PS 5.1 syntax, no `&&`, `??` or ternary). They all need a run on a Windows host.

## Fixed Issues

### WR-01: reg unload retry never retried
**Files modified:** `deploy/windows/install-agent.ps1`, `deploy/windows/uninstall-agent.ps1`
**Commit:** e5fe782
**Applied fix:** New `Invoke-RegExe` helper sets `$ErrorActionPreference='Continue'` for the call, discards output with `*> $null` and returns `$LASTEXITCODE`. It is used for both `reg load` and `reg unload`. The `finally` block now tries up to 5 times with GC and a 2 s sleep between tries, is wrapped so it can never throw, and falls back to a warning with the manual unload command.

### WR-02: unvalidated -InstallPath with robocopy /MIR and recursive delete
**Files modified:** all four `deploy/windows/*.ps1`
**Commit:** 0d9ee39
**Applied fix:** `Assert-SafeInstallPath` runs before elevation and the install or delete. The path must be absolute with a drive letter, contain a folder segment named `CafeManager`, must not be a drive root, and must not be a system folder or inside the Windows folder (SystemRoot, Program Files, Program Files (x86), ProgramData, Users, USERPROFILE). The install scripts also refuse a source folder that contains or equals the install folder. Both robocopy calls now use `/R:3 /W:2`, and exit codes of 8 or more already threw.
**Decision:** There is no override switch. A custom path must sit under a `CafeManager` folder. `docs/INSTALL.md` now says so (commit 7585985).

### WR-03: elevation relaunch quoting
**Files modified:** all four `deploy/windows/*.ps1`
**Commit:** c829f34
**Applied fix:** `Quote-Arg` was removed. `Quote-NativeArg` (doubles backslashes before a quote and trailing backslashes, escapes quotes) is now defined in all four scripts and used for the relaunch. New `Remove-TrailingBackslash` trims trailing `\` from `-SourcePath` and `-InstallPath` (not for `C:\`), so robocopy no longer sees an escaped quote.

### WR-04: agentsettings.json exposed during write and weak model for administrator gaming users
**Files modified:** `deploy/windows/install-agent.ps1`
**Commit:** 517b728
**Applied fix:** The script creates an empty temp file in the install folder, applies the ACL (SYSTEM and Administrators full, gaming user read, inheritance removed), writes the JSON, deletes any old settings file and moves the temp file into place. A rename keeps the temp file's ACL, so entries for a previous gaming user cannot linger. A `finally` block removes the temp file on failure. New `Test-IsLocalAdministrator` (direct member of S-1-5-32-544 via `Get-LocalGroupMember`) prints two warnings when the gaming user is a local administrator.
**Decision:** This only warns, as directed. Nested group membership is not detected, and an unreadable group counts as "not an administrator".

### WR-05: non-numeric Backup:Hour or Backup:Keep crashed startup
**Files modified:** `src/Cafe.Server/Backups/BackupOptions.cs`, `tests/Cafe.Tests/BackupServiceTests.cs`
**Commit:** 4420d5f
**Applied fix:** `Hour` and `Keep` use `int.TryParse` with the invariant culture, and `RunScheduler` uses `bool.TryParse`. A bad value logs a warning through the `warn` callback and uses the default (4, 14, true). The existing range clamps stay. New theory test covers `4am`, `abc` and `1.5`.

### WR-06: production layout on non-Windows targeted /usr/share
**Files modified:** `src/Cafe.Server/Hosting/CafeDataPaths.cs`, `src/Cafe.Server/Program.cs`, `tests/Cafe.Tests/CafeDataPathsTests.cs`
**Commit:** 2e9c9e6
**Applied fix:** New `CafeDataPaths.DefaultAppDataRoot()` returns CommonApplicationData on Windows and LocalApplicationData elsewhere, with a temp-folder fallback if the folder is empty. `Program.cs` uses it. `Cafe:DataDirectory` still overrides it, and `EnsureDirectories()` still creates the folder. New unit test.

### WR-07: EF SQL commands logged at Information
**Files modified:** `src/Cafe.Server/appsettings.json`, `src/Cafe.Server/Program.cs`
**Commit:** dd58b93
**Applied fix:** `appsettings.json` sets `Microsoft.EntityFrameworkCore` to Warning and `Microsoft.Hosting.Lifetime` to Information. In the production file-logging branch, `Program.cs` adds a Warning filter for `Microsoft.EntityFrameworkCore` unless configuration sets that category explicitly. No new test, because this is wiring.

### WR-08: failed upgrade left service stopped, and ACL locked out a co-located agent
**Files modified:** `deploy/windows/install-server.ps1`
**Commit:** d43fd67
**Applied fix:** Steps 3 to 6 (copy, ACL, service, firewall) are inside `try/catch`. On failure, if the service existed before, the script tries `Start-Service`, prints whether that worked, then rethrows a clear message. The main handler still exits 1. After the restrictive ACL on `%ProgramData%\CafeManager`, the script creates `...\agent` and grants Users Modify with `(OI)(CI)` on that subfolder only. The database folder stays restricted to SYSTEM and Administrators.
**Decisions:**
- The restart covers failures in steps 3 to 6 only. A failure to start the new service in step 7 is not retried.
- The restarted service may be running partly mirrored files if robocopy failed midway. The message says the upgrade was not completed.
- Users Modify on `agent` means any local user can alter the emergency lockout state file. This is accepted because the agent runs as the gaming user and must write it, and it matches the guidance.

### WR-09: Compress-Archive backslash entries
**Files modified:** `build/build-release.ps1`
**Commit:** bbdd73f
**Applied fix:** The zip is written entry by entry with `System.IO.Compression.ZipArchive` and `ZipFileExtensions.CreateEntryFromFile`. Entry names are relative to `dist\stage`, so they start with the bundle folder, use forward slashes and are sorted. `Compress-Archive` is gone.
**Not changed:** The AgentSim is still published only for win-x64 in the `.ps1`, while the `.sh` publishes for the host OS. The review only mentioned this in passing.

### IN-04: log directory created on every write, failures silent
**Files modified:** `src/Cafe.Server/Hosting/FileLoggerProvider.cs`, `tests/Cafe.Tests/FileLoggerProviderTests.cs`
**Commit:** fcce32f
**Applied fix:** The directory is created lazily on the first write and then cached. If creation fails it is retried at most every 30 s. A `DirectoryNotFoundException` on append resets the cache. Failures set a public `LastError` and go to `Trace.WriteLine`, and a successful write clears `LastError`. The constructor still does not create the folder, which an existing test relies on. New test covers the unwritable-folder to recovery path.
**Not done:** The review also suggested showing `LastError` on the Settings page. That is not wired up.

### IN-06: uninstall removed any empty parent folder
**Files modified:** `deploy/windows/uninstall-server.ps1`, `deploy/windows/uninstall-agent.ps1`
**Commit:** 76dc6a2
**Applied fix:** The parent folder is removed only when its leaf name is `CafeManager` (case-insensitive) and it is empty.

### IN-07: release script gaps
**Files modified:** `build/build-release.sh`, `build/build-release.ps1`
**Commit:** 2b4ab7b
**Applied fix:**
- `SKIP_TESTS=1` now produces `CafeManager-<version>-win-x64-untested.zip` in both scripts.
- The safety scan also rejects `*.sqlite`, `*.sqlite3` and `*.tmp`.
- The `-ErrorAction SilentlyContinue` was removed from the `docs\*.md` copy in the `.ps1`.
- The `.sh` OS/arch check now runs before any publish.

## Final verification

- `dotnet build Cafe.sln`: 0 warnings, 0 errors.
- `dotnet test Cafe.sln`: 1059 passed, 0 failed (first run, no flaky retry needed).
- `build/build-release.sh` (strict, with tests): succeeded, and the safety scan accepted the new `*.sqlite`, `*.sqlite3` and `*.tmp` patterns.
  - `dist/CafeManager-1.0.0-win-x64.zip`: 121,405,677 bytes (about 116 MB)
  - SHA-256: `aa755ee1292e5ddc17b599eb1581ce37aa69b4d8fc06d6edf7b460f931b6fd5b`
  - `dist/` is gitignored, so the zip is not committed.
- Status flag: all PowerShell fixes (WR-01 to WR-04, WR-08, WR-09, IN-06, IN-07) and WR-07 are logic or wiring changes without Windows verification. They need a test on a Windows host before shipping, in particular the hive load and unload retry, the temp-file ACL and rename, the failed-upgrade restart, and the zip layout from `build-release.ps1`.

---

_Fixed: 2026-11-20_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
