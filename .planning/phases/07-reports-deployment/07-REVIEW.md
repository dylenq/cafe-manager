---
phase: 07-reports-deployment
reviewed: 2026-11-20T00:00:00Z
depth: standard
files_reviewed: 24
files_reviewed_list:
  - build/build-release.ps1
  - build/build-release.sh
  - build/bundle-README.txt
  - deploy/windows/install-agent.ps1
  - deploy/windows/install-server.ps1
  - deploy/windows/uninstall-agent.ps1
  - deploy/windows/uninstall-server.ps1
  - src/Cafe.Server/Backups/BackupOptions.cs
  - src/Cafe.Server/Backups/BackupService.cs
  - src/Cafe.Server/Components/Pages/Reports.razor
  - src/Cafe.Server/Components/Shared/BackupPanel.razor
  - src/Cafe.Server/Components/Shared/RevenueBarChart.razor
  - src/Cafe.Server/Hosting/CafeDataPaths.cs
  - src/Cafe.Server/Hosting/FileLoggerProvider.cs
  - src/Cafe.Server/Program.cs
  - src/Cafe.Server/Reports/CsvWriter.cs
  - src/Cafe.Server/Reports/ReportExport.cs
  - src/Cafe.Server/Reports/ReportModels.cs
  - src/Cafe.Server/Reports/ReportRange.cs
  - src/Cafe.Server/Reports/ReportService.cs
  - src/Cafe.Server/Reports/ReportServiceCollectionExtensions.cs
  - src/Cafe.Server/appsettings.json
  - Directory.Build.props
  - .gitignore
findings:
  critical: 0
  warning: 9
  info: 7
  total: 16
status: issues_found
---

# Phase 7: Code Review Report

**Reviewed:** 2026-11-20
**Depth:** standard
**Files Reviewed:** 24 source and script files. The test files and Razor CSS files in the phase were not reviewed for defects.
**Status:** issues_found

## Summary

No blockers. Report aggregation, range validation, CSV escaping, export auth and the SVG chart are sound. The `is '=' or '+' ...` pattern in `CsvWriter` binds correctly. The SVG chart only emits Razor-encoded text plus numbers formatted with the invariant culture. The export endpoint whitelists the section name and requires authorization. Local-day and DST bounds use local-midnight arithmetic, and weeks start on Monday.

The main risks are in the installers and ops code:
- The `reg unload` retry loop can never retry on Windows PowerShell 5.1.
- `robocopy /MIR` runs against an unvalidated install path.
- The relaunch argument quoting breaks on some secrets.
- A malformed `Backup:Hour` crashes the service at startup.
- Production on a non-Windows host tries to create `/usr/share/CafeManager`.
- Production logging records every EF SQL command at Information level.

Only the Reports page and CSV export were traced. No Windows host was available, so PowerShell behaviour is reasoned from known Windows PowerShell 5.1 semantics and was not run.

## Warnings

### WR-01: `reg unload ... 2>&1` under `$ErrorActionPreference='Stop'` throws, so the unload retry never retries
**File:** `deploy/windows/install-agent.ps1:160`, `deploy/windows/uninstall-agent.ps1:112`
**Issue:** In Windows PowerShell 5.1, merging a native command's stderr with `2>&1` while `$ErrorActionPreference = 'Stop'` turns the first stderr line into a terminating NativeCommandError. When `reg unload` fails (the "Access is denied" case the code anticipates), the exception escapes the `finally` block. The retry loop and the `Write-Warning` fallback never run, and the original error is masked. The temporary hive `HKU\CafeManagerTemp` can stay loaded, which makes the next run's `reg load` fail.
**Fix:**
```powershell
$prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
try { & reg.exe unload "HKU\$TempHiveName" *> $null } finally { $ErrorActionPreference = $prev }
if ($LASTEXITCODE -eq 0) { $unloaded = $true } else { Start-Sleep -Seconds 2 }
```

### WR-02: `robocopy /MIR` and `Remove-Item -Recurse` run against an unvalidated `-InstallPath`
**File:** `deploy/windows/install-server.ps1:127`, `deploy/windows/install-agent.ps1:266`, `deploy/windows/uninstall-server.ps1:105`, `deploy/windows/uninstall-agent.ps1:144`
**Issue:** `-InstallPath C:\Program Files` or `D:\` makes `/MIR` delete everything in it that is not in the bundle. The uninstall scripts do the same with a recursive delete. `/MIR` also has no `/R:n /W:n`. Robocopy defaults to 1,000,000 retries with a 30 s wait, so a locked DLL (antivirus, or a process that did not exit) hangs the installer for a very long time.
**Fix:** Refuse to proceed unless the path's leaf is `Server` or `Agent` under a `CafeManager` folder, or require `-Force` for any other path. Add `/R:3 /W:2` to every robocopy call.

### WR-03: Elevation relaunch uses `Quote-Arg`, which breaks on trailing backslashes and quotes in secrets
**File:** `deploy/windows/install-agent.ps1:230`, also `:99` and the same helper in the other three scripts
**Issue:** `Quote-Arg` escapes a quote as `""` and ignores trailing backslashes. An `-EmergencyCode` or `-SharedKey` ending in `\` becomes `"abc\"`. The closing quote is then consumed and the following arguments merge into this one. The elevated run fails with a confusing parameter error or receives a wrong value. The correct `Quote-NativeArg` already exists in `install-agent.ps1` (line 104) but is not used here. `-SourcePath` or `-InstallPath` passed with a trailing backslash breaks the same way, and in the robocopy calls too.
**Fix:** Use `Quote-NativeArg` for the relaunch in all four scripts. Trim trailing `\` from path parameters with `.TrimEnd('\')`.

### WR-04: `agentsettings.json` is briefly readable by every local user, and the "read-only for the gaming user" model fails if that user is an administrator
**File:** `deploy/windows/install-agent.ps1:310-311`
**Issue:**
- The file is written with the inherited `Program Files` ACL (Users can read) before `icacls /inheritance:r` runs. The SharedKey and the emergency hash are exposed for that window.
- On re-install, `/grant:r` does not remove an old explicit entry for a previous gaming user.
- The default gaming user is whoever ran the script, usually an administrator. Administrators have Full access (S-1-5-32-544 is granted `F`) and the task runs `-RunLevel Highest`, so that user can edit the settings and the agent. Per-user `DisableTaskMgr` is also only a speed bump. The installer never warns about this.

**Fix:** Create the file empty, apply the ACL, then write the content. Run `icacls /reset` first. Warn or fail when `$sid` is a member of Administrators (check `[Security.Principal.WindowsPrincipal]` for that account, or use `net localgroup`).

### WR-05: `Backup:Hour` and `Backup:Keep` with a non-numeric value crash the service at startup
**File:** `src/Cafe.Server/Backups/BackupOptions.cs:14,21`
**Issue:** `configuration.GetValue("Backup:Hour", DefaultHour)` throws `InvalidOperationException` when the value is not an int (for example `"4am"`). `BackupService` is built when the host starts, so the whole server (and every station's timer) fails to start. The out-of-range handling below it suggests the intent was to warn and fall back.
**Fix:** Use `int.TryParse(configuration["Backup:Hour"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour)` and fall back with `warn`. Do the same for `RunScheduler` with `bool.TryParse`.

### WR-06: Production layout on a non-Windows host targets `/usr/share/CafeManager`
**File:** `src/Cafe.Server/Hosting/CafeDataPaths.cs:41-60`, `src/Cafe.Server/Program.cs:30-38`
**Issue:** `CommonApplicationData` is `/usr/share` on Linux and macOS. Running with `ASPNETCORE_ENVIRONMENT=Production` there (a plain `dotnet Cafe.Server.dll` defaults to Production) makes `EnsureDirectories()` throw `UnauthorizedAccessException` at startup. A non-root dev machine cannot test a production-like run. If it runs as root, data lands in an unexpected system directory.
**Fix:** On non-Windows, resolve the root from `LocalApplicationData`, or require `Cafe:DataDirectory` when the layout is production and `!OperatingSystem.IsWindows()`.

### WR-07: Production file logger records every EF SQL command at Information level
**File:** `src/Cafe.Server/appsettings.json:2-6`, `src/Cafe.Server/Program.cs:40-43`, `src/Cafe.Server/Hosting/FileLoggerProvider.cs:21`
**Issue:** Only `Microsoft.AspNetCore` is raised to Warning. `Microsoft.EntityFrameworkCore.Database.Command` therefore logs every executed command at Information, including the periodic ticker and report queries. The daily log file grows quickly and each line costs a synchronous `File.AppendAllText` under a lock. That is the same I/O path as request handling. Parameter values are masked, but the SQL text of every query lands on disk.
**Fix:** Add `"Microsoft.EntityFrameworkCore": "Warning"` and `"Microsoft.Hosting.Lifetime": "Information"` to the Logging section. Consider buffering writes in a channel.

### WR-08: Server installer can leave the service stopped after a failed upgrade copy, and the ACL change locks out an agent on the same PC
**File:** `deploy/windows/install-server.ps1:120-135`
**Issue:**
- The service is stopped before the copy. If robocopy, icacls or `sc.exe` throws, the cafe is left with no server and no rollback or restart attempt.
- `icacls /inheritance:r` with only SYSTEM and Administrators on `C:\ProgramData\CafeManager` also cuts off the gaming user's access to `...\CafeManager\agent`. The agent writes its emergency lockout state there (`EmergencyAttemptTracker`) when the server PC is also a gaming PC. It cannot persist the counter or create the folder.

**Fix:** Wrap steps 3 to 6 in `try/catch` and `Start-Service` on failure if the service existed. Either apply the ACL to everything except `agent`, or have the agent installer grant its user Modify on `ProgramData\CafeManager\agent` after the server ACL is set.

### WR-09: `Compress-Archive` on PowerShell 5.1 writes zip entries with backslash separators
**File:** `build/build-release.ps1:106`
**Issue:** Windows PowerShell 5.1's `Compress-Archive` is known to store entry names with `\` and to fail on files held open. The Windows PS script produces a different archive from the `.sh` script (`zip -X`), and some extractors (macOS Archive Utility, `unzip`) flatten the layout. `install-*.ps1` then cannot find `Server\` or `Agent\`. The AgentSim is also built only for `win-x64` here, whereas the `.sh` builds it for the host OS.
**Fix:** Use `[System.IO.Compression.ZipFile]::CreateFromDirectory($BundlePath, $Zip, 'Optimal', $true)` with forward-slash entry names written explicitly. Unlike `Compress-Archive`, this keeps the layout identical to the `.sh` output.

## Info

### IN-01: Report aggregation: payments to a later-voided order are not possible, but order-less allocations make products not sum to revenue
**File:** `src/Cafe.Server/Reports/ReportService.cs:166-174`, `:73-77`
**Issue:** The product and category totals (cash basis by allocation) skip orders missing from `orderIndex`, orders with `Total == 0` and orders without lines. Other tabs can then total more than the product tab. This cannot happen for voids (`OrderService.VoidAsync` refuses orders that have allocations), but it can for orders whose lines were later removed or deleted. No test asserts that the product total plus time and package revenue equals `Collected`.
**Fix:** Add an "Other / unallocated" product row for the difference, or a property test of that invariant.

### IN-02: Midnight in an ambiguous DST fall-back would mismatch `ToUtcBounds` and `LocalDate`
**File:** `src/Cafe.Server/Reports/ReportRange.cs:99-107`, `src/Cafe.Server/Reports/ReportService.cs:196-197`
**Issue:** For zones whose fall-back transition crosses midnight, `ConvertTimeToUtc` picks the standard-time (later) instant for the range start. Payments in the first pass of that hour are excluded from the day that `LocalDate` assigns them. Total Collected and the by-method figures can then differ. Irrelevant for most zones.
**Fix:** Document the limitation, or resolve ambiguity with `GetAmbiguousTimeOffsets().Max()` explicitly.

### IN-03: The 400-day cap bounds output rows, not query work
**File:** `src/Cafe.Server/Reports/ReportRange.cs:18`, `src/Cafe.Server/Reports/ReportService.cs:30-90`
**Issue:** `ReportService.GetAsync` loads every payment, allocation, session, order and package sale in the database before filtering in memory. The range cap does not bound that work. The cap is correct for a cafe-sized database, but the "caps the work a single page view or export can trigger" comment overstates it. This is not a v1 performance finding.
**Fix:** Reword the comment.

### IN-04: `FileLoggerProvider` creates the log directory on every write and is silent when it cannot write
**File:** `src/Cafe.Server/Hosting/FileLoggerProvider.cs:60-70`
**Issue:** `Directory.CreateDirectory` runs per log call. A permission failure is swallowed with no fallback to a different directory, the console or the event log, so logs can vanish with no hint.
**Fix:** Create the directory once at construction, and keep an in-memory last-error flag the Settings page can show.

### IN-05: Backup file names have minute resolution, and sort order is by local time
**File:** `src/Cafe.Server/Backups/BackupService.cs:121-124,208-212`
**Issue:**
- Two backups in the same minute overwrite each other. This is harmless for a nightly backup plus a manual one.
- At the autumn DST fall-back the repeated local hour produces names that sort against the real order.
- If the clock goes backwards, Prune removes the file just written.
- A user's own file named like `cafe-20250101-0400.db` in the folder is treated as a managed backup and pruned.

**Fix:** Use UTC in file names (`yyyyMMdd-HHmmss`), or record a marker file next to the backups.

### IN-06: Uninstall scripts remove the parent folder whenever it is empty, regardless of its name
**File:** `deploy/windows/uninstall-agent.ps1:204-210`, `deploy/windows/uninstall-server.ps1:107-113`
**Issue:** With a custom `-InstallPath` such as `D:\Agent`, `Split-Path -Parent` gives `D:\` and the script tries to remove an empty drive root or an unrelated empty folder.
**Fix:** Only delete the parent when `(Split-Path -Leaf $parent) -eq 'CafeManager'`.

### IN-07: Release script gaps: tests can be skipped silently, bundle docs are copied unfiltered
**File:** `build/build-release.sh:28-33`, `build/build-release.ps1:92-94`
**Issue:**
- `SKIP_TESTS=1` still produces a normally named zip that looks shippable. Only a warning is printed.
- `build-release.ps1` hides copy errors for `docs\*.md` with `-ErrorAction SilentlyContinue`.
- The `.sh` simulator `case` fails only after the long server publish has already run.
- The safety scan checks `*.db`, `agentsettings.json` and `logs` only. It would miss `*.sqlite` or backup `*.tmp` files.

**Fix:** Name the bundle `...-UNTESTED.zip` when tests are skipped. Do the OS/arch check before publishing. Extend the scan to `*.sqlite` and `*.tmp`.

---

_Reviewed: 2026-11-20_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
