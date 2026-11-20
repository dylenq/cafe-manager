---
phase: 07-reports-deployment
plan: 05
subsystem: documentation
tags: [docs, readme, release-bundle]
requires: [07-02, 07-03, 07-04]
provides:
  - docs/INSTALL.md (owner install and operations guide)
  - docs/USER-GUIDE.md (front-desk staff guide)
  - README.md (overview, dev quick start, layout, configuration)
  - dist/CafeManager-1.0.0-win-x64.zip (final release bundle, gitignored)
affects: []
tech-stack:
  added: []
  patterns: [docs verified against script parameters and UI labels]
key-files:
  created:
    - docs/INSTALL.md
    - docs/USER-GUIDE.md
  modified:
    - README.md
key-decisions:
  - "INSTALL.md recommends machine environment variables (Cafe__TimeZone, Cafe__Culture) over editing appsettings.json, because install-server.ps1 mirrors the install folder with robocopy /MIR and an upgrade replaces appsettings.json"
  - "USER-GUIDE.md states there is no admin password reset path (AdminPasswordService has none); the owner is told it cannot be reset from the app"
status: complete
commits: 3
plan_head_before: a0aa934cdcd6802bedd42e9b09971a9b26178aa1
plan_head_after: aae7d9d8298be1448142e3232710265965e4e775
actuals:
  tokens: 30000
  tasks: 3
  commits: 3
completed: 2026-11-20
---

# Phase 7 Plan 05: Documentation and final release bundle Summary

Owner and staff documentation written from the real scripts, settings keys and Razor labels, README rewritten, and the strict release build (full test suite, docs required) produced `dist/CafeManager-1.0.0-win-x64.zip` containing both guides.

## Tasks

| Task | Name | Commit |
|------|------|--------|
| 1 | docs/INSTALL.md | c2d0adc |
| 2 | docs/USER-GUIDE.md | 3b3805d |
| 3 | README.md and final strict release build | aae7d9d |

## Final build

- Command: `build/build-release.sh` with no skip flags, exit 0.
- Tests: 1054 passed, 0 failed, 0 skipped (Cafe.Tests, net10.0). No flaky test needed a rerun.
- Agent publish mode: self-contained (no FRAMEWORK-DEPENDENT.txt).
- Zip: `/Users/dylenquyn/Cafe/dist/CafeManager-1.0.0-win-x64.zip`, 121,398,770 bytes (about 116 MiB).
- SHA-256: `2a059059bd79bdf50d6256b6e9ec33077da5bb5653596984815a1da069498ace`
- Contents include: `install-server.ps1`, `uninstall-server.ps1`, `install-agent.ps1`, `uninstall-agent.ps1`, `README.txt`, `docs/INSTALL.md`, `docs/USER-GUIDE.md`, `Server/Cafe.Server.exe`, `Agent/Cafe.Agent.exe`, `tools/AgentSim-osx-arm64/`.
- `dist/` is gitignored. All relative links in README.md and the docs resolve. The only `SharedKey` mentions are placeholders.

## Deviations from Plan

None. The plan said "commit nothing" per task, but the executor contract requires per-task commits, so each file was committed on its own.

## Docs omissions and caveats

- No password-reset path exists, so the guide says the admin password cannot be reset from the app.
- The USER-GUIDE does not describe the Sessions page history tab beyond the "Agent commands" tab mention.
- Windows printer mode sends raw ESC/POS; INSTALL.md says the printer must understand ESC/POS. Non-ESC/POS printers must use Browser mode.
- Settings keys `Tv:PowerOffOnStop`, `Receipts:*`, grace and warning minutes are database settings edited in the app, not appsettings keys; the docs describe them by their UI labels.

## Human checks still open (Windows only)

- Run `install-server.ps1` on a Windows 10/11 PC: service created, delayed start, firewall rule, localhost setup. (DEPL-01)
- Run `install-agent.ps1` on a Windows gaming PC: scheduled task, Task Manager policy, lock and unlock, emergency unlock.
- Walk through the docs on a real Windows setup, including SmartScreen steps, Samsung TV pairing and a receipt printer.

## Known Stubs

None.

## Self-Check: PASSED

docs/INSTALL.md, docs/USER-GUIDE.md, README.md and the zip exist; commits c2d0adc, 3b3805d and aae7d9d exist.
