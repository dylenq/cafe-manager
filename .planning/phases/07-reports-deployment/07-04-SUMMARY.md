---
phase: 07-reports-deployment
plan: 04
subsystem: deployment
tags: [powershell, scheduled-task, release-bundle, bash]
requires: [07-03]
provides:
  - deploy/windows/install-agent.ps1 and uninstall-agent.ps1
  - build/build-release.sh and build-release.ps1 (release bundle builder)
  - build/bundle-README.txt (bundle README.txt)
  - Version 1.0.0 in Directory.Build.props
affects: [07-05]
tech-stack:
  added: []
  patterns: [self-elevating script, offline NTUSER.DAT hive load with retrying unload, redirected-output capture of a WinExe]
key-files:
  created:
    - deploy/windows/install-agent.ps1
    - deploy/windows/uninstall-agent.ps1
    - build/build-release.sh
    - build/build-release.ps1
    - build/bundle-README.txt
  modified:
    - Directory.Build.props
key-decisions:
  - "robocopy of the agent excludes agentsettings.json and logs so a re-run never wipes them; the installer rewrites the settings file anyway"
  - "Emergency code is passed to --hash-code with CommandLineToArgvW quoting (Quote-NativeArg), separate from the elevation quoting"
  - "build-release.sh pipes unzip into sed, not head, because head closes the pipe early and pipefail turned that into exit 141"
status: complete
commits: 3
plan_head_before: 516a3c3265596a2fee8004f1b053399ac2712d63
plan_head_after: 061b7267c73ffdb30cb615eafd084c1d6ae0e99e
actuals:
  tokens: 28000
  tasks: 3
  commits: 3
completed: 2026-11-20
---

# Phase 7 Plan 04: Agent installer and release bundle Summary

Agent install/uninstall scripts (logon task, hashed emergency code, Task Manager policy via live or offline hive) and a release builder that ran here and produced `dist/CafeManager-1.0.0-win-x64.zip`.

## Tasks

| Task | Commit | Result |
|------|--------|--------|
| 1 install-agent.ps1 / uninstall-agent.ps1 | 1568d38 | Done, grep gates pass, ASCII only |
| 2 Version, build scripts, bundle README | 78ee3a1 | Done, `bash -n` ok, solution builds |
| 3 Run the release build | 061b726 (script fix) | Zip produced and inspected |

## Release build result

- Agent mode: **self-contained** (the WPF cross-publish from macOS worked; no FRAMEWORK-DEPENDENT.txt was produced, so that fallback path was not exercised).
- Zip: `dist/CafeManager-1.0.0-win-x64.zip`, 121,386,304 bytes (128M)
- SHA-256: `d1d2df12f1cd8761e9021eb1d194da41e4b00e3d326bb01773fc765be558df11` (of the final build, run with SKIP_TESTS=1 right after a full run that had passed the suite)
- Contents confirmed: `CafeManager-1.0.0-win-x64/` with `Server/Cafe.Server.exe`, `Server/appsettings.json`, `Server/wwwroot/`, `Agent/Cafe.Agent.exe`, `Agent/agentsettings.json` (SharedKey empty), the four `.ps1` scripts and `README.txt` at the root, `tools/AgentSim-osx-arm64/`. No `.db`, `-wal` or `-shm` files. `docs/` is empty (docs arrive in Plan 07-05; built with `CAFE_ALLOW_MISSING_DOCS=1`).
- `dist/` does not appear in `git status`.

## Deviations from Plan

**1. [Rule 1 - Bug] unzip | head killed the script with SIGPIPE**
- **Found during:** Task 3 (second run; the first run failed only on a flaky test)
- **Issue:** The summary step piped `unzip -l` into `head -n 40`; under `set -o pipefail` the early pipe close gave exit 141 after the zip was already written.
- **Fix:** `unzip -l "$ZIP" | sed -n '1,40p'` (reads all input).
- **Files modified:** build/build-release.sh
- **Commit:** 061b726

**2. Test flake (no code change):** `AgentLifecycleIntegrationTests.Manual_commands_reach_the_agent_and_are_audited` timed out on the first release run (known flaky timing test). A rerun passed all 1054 tests and the build continued to the zip. The final confirmation build used `SKIP_TESTS=1` to avoid a third multi-minute suite run after the unzip fix.

## Human-only checks (not run: no Windows host, no pwsh)

- Run install-agent.ps1 on a Windows gaming PC with the gaming user logged out, then in: confirm the "CafeManager Agent" task (At log on, highest privileges, restart every minute), Task Manager disabled for that user, `agentsettings.json` with a `pbkdf2-sha256$...` hash and read-only ACL for the gaming user, PC online on the dashboard, agent starts locked after reboot and logon.
- Re-run the installer (upgrade) and run uninstall-agent.ps1.
- PowerShell syntax of both agent scripts and build-release.ps1 was only reviewed by hand and by grep (ASCII, no `&&`, no `??`).

## Threat model

T-07-12 (settings ACL: SYSTEM/Administrators full, gaming user read), T-07-13 (build safety scan for .db, populated SharedKey, log files), T-07-14 (code never echoed, only the hash persisted), T-07-16 (hive always unloaded in a finally block with 3 retries) are implemented as planned.

## Known Stubs

None.

## Self-Check: PASSED

All five created files and the modified props file exist; commits 1568d38, 78ee3a1 and 061b726 exist; the zip exists with the expected entries.
