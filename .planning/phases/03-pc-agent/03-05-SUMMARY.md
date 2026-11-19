---
phase: 03-pc-agent
plan: 05
subsystem: agent
tags: [wpf, windows, lock-screen, keyboard-hook, emergency-unlock, toast]
requires:
  - phase: 03-pc-agent
    provides: AgentController, EmergencyCodeHasher, AgentSettingsLoader (plan 03-03)
provides:
  - Cafe.Agent WPF app (net10.0-windows, EnableWindowsTargeting) that builds on macOS
  - KeyBlockPolicy and AgentCommandLine (platform-neutral, tested)
  - README section "Running the PC agent"
affects: [07-installer]
status: complete
commits: 3
plan_head_before: 1784cc7bbd40827e2cd9a500cd0f1d0c006c6ca7
plan_head_after: b5e0444b67b68eb863bf8330934cd2f1c6f11902
actuals:
  tokens: 40000
  tasks: 3
  commits: 3
tech-stack:
  added: [Microsoft.Extensions.Logging 10.0.15 (Cafe.Agent only)]
  patterns:
    - "All windows built in C# code (no XAML); monitors via Win32 EnumDisplayMonitors, no WinForms"
    - "Keyboard hook installed only while locked; callback swallows on KeyBlockPolicy and passes keys through on any exception"
    - "Every ILockScreen/IWarningDisplay call marshalled to the UI dispatcher with BeginInvoke"
key-files:
  created:
    - src/Cafe.Agent.Core/AgentCommandLine.cs
    - src/Cafe.Agent.Core/KeyBlockPolicy.cs
    - src/Cafe.Agent/ (csproj, Program, AgentApplication, SingleInstance, FileLoggerProvider, PowerControl, KeyboardBlocker, LockWindow, EmergencyUnlockDialog, WpfLockScreen, WarningToast, WpfWarningDisplay, Native/, agentsettings.json)
    - tests/Cafe.Tests/AgentCommandLineTests.cs
    - tests/Cafe.Tests/KeyBlockPolicyTests.cs
  modified:
    - Cafe.sln
    - README.md
requirements-completed: []
---

# Phase 3 Plan 05: Windows PC agent Summary

WPF agent that locks every monitor, swallows the escape chords with a low-level hook active only while locked, shows a focus-safe countdown toast, runs shutdown/restart, and supports a hashed emergency unlock; compiles on macOS and is documented for manual rollout.

## What was built

1. **Skeleton (da5ed21):** `AgentCommandLine` (`--hash-code`, `--help`, errors never throw) with 7 tests; `Cafe.Agent.csproj` (WinExe, UseWPF, EnableWindowsTargeting, no WinForms, only Microsoft.Extensions.Logging 10.0.15) added to `Cafe.sln`; `Program` handles CLI via `AttachConsole` without opening a window, named mutex `Global\CafeManagerAgent` (second instance exits 3 and logs); file logger (14-day trim); `PowerControl` via `shutdown.exe`; `AgentApplication` (OnExplicitShutdown, never handles SessionEnding, fails secure by showing the lock screen with the configuration error and never starting the controller).
2. **Lock screen (42aa8e6):** `KeyBlockPolicy` (Win L/R, Alt+Tab, Alt+Esc, Ctrl+Esc, Alt+F4, Ctrl+Shift+Esc; Ctrl+Alt+Shift+F12 never blocked) with 18 tests; `KeyboardBlocker` (WH_KEYBOARD_LL, pinned delegate, exceptions pass the key through); per-monitor `LockWindow` positioned in physical pixels, not in taskbar, close cancelled, re-activates on deactivation; `EmergencyUnlockDialog` with a cross-instance attempt tracker (3 wrong codes then 30 s lockout, code never logged); `WpfLockScreen` rebuilds windows on `DisplaySettingsChanged`.
3. **Toast and docs (b5e0444):** `WarningToast` (WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW, ShowActivated false, red "Time is up — save your work" in Grace, plain TextBlock text only); `WpfWarningDisplay` (exclamation sound in try/catch, messages auto-hide after 10 s unless a countdown is active); real implementations wired in `AgentApplication`; README "Running the PC agent" with config, hash tool, startup, behavior, emergency unlock, troubleshooting, simulator and Phase 7 boundary, plus a clear-text key note in Security note.

## Verification

- `dotnet build Cafe.sln`: succeeded, no errors, no warnings. The Windows targeting pack restored fine on macOS with `EnableWindowsTargeting=true`.
- `dotnet test Cafe.sln`: 423 passed, 0 failed (no flake this run).
- Acceptance greps: EnableWindowsTargeting/UseWPF present, no UseWindowsForms, OnExplicitShutdown present, NOACTIVATE and "Time is up" present, no markup/Hyperlink types in toast, nothing logs the code.

## Deviations from Plan

**1. [Rule 3 - Blocking] WPF SDK drops the System.IO implicit using.** Added explicit `using System.IO;` in Program, FileLoggerProvider and AgentApplication. Commit da5ed21.

**2. [Rule 3 - Blocking] `AgentApplication.Exit()` hid `Application.Exit` (CS0108).** Renamed to `ExitAgent()`. Commit da5ed21.

**3. [Design] Task 1 used temporary no-op display factories** so task 1 built before the WPF classes existed; task 3 replaced them with direct wiring and removed the null classes.

**4. [Design] Emergency attempt state lives in `EmergencyAttemptTracker` (in EmergencyUnlockDialog.cs)**, owned by `WpfLockScreen`, so closing and reopening the prompt cannot reset the 3-attempt lockout.

Otherwise the plan was executed as written.

## Known Stubs

None. (The emergency unlock is intentionally disabled when `EmergencyUnlockCodeHash` is empty, as specified.)

## Threat Flags

None beyond the plan's threat model.

## Human checks pending

All real Windows behavior, none runnable on macOS:

1. Lock: every monitor shows a lock window with cafe name, station, reason and clock; Win, Alt+Tab, Alt+Esc, Ctrl+Esc, Alt+F4 and Ctrl+Shift+Esc do nothing; windows are not in the taskbar and cannot be closed.
2. Emergency unlock: Ctrl+Alt+Shift+F12 opens the prompt; 3 wrong codes lock it for 30 s; the correct code (hash from `--hash-code`) unlocks; "Also exit the agent" exits; empty hash shows "not configured".
3. Multi-monitor: plugging or unplugging a monitor while locked keeps every screen covered.
4. Toast: appears bottom right at the warning minutes over a fullscreen game without minimizing it or taking keystrokes; turns red with "Time is up — save your work" during grace; PC locks after grace.
5. Power: shutdown and restart from the dashboard PC menu execute.
6. Resilience: stopping the server mid-session keeps the countdown and locks at zero; restart reconnects within about 30 s; bad or missing `agentsettings.json` shows the configuration error on the lock screen; a second instance exits immediately; Windows shutdown/logoff is not blocked.
7. `Cafe.Agent.exe --hash-code 1234` from a terminal prints a hash (the AttachConsole output path in a WinExe has not been seen).
8. Package legitimacy: confirm on nuget.org that Microsoft.Extensions.Logging 10.0.15 is owned by Microsoft (T-03-SC).

## Notes for the orchestrator

- REQUIREMENTS.md not changed: AGNT-02, AGNT-04, AGNT-05 and AGNT-06 are implemented in code but unverified on Windows hardware (and AGNT-05's server side is plan 03-04), so they should be marked complete only after verification.
- STATE.md and ROADMAP.md untouched, as instructed.

## Self-Check: PASSED

- Files exist: all listed Cafe.Agent sources, KeyBlockPolicy.cs, AgentCommandLine.cs, both test files, README section.
- Commits exist: da5ed21, 42aa8e6, b5e0444 (3 measured via rev-list from 1784cc7).
