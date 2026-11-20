---
phase: 03-pc-agent
verified: 2026-11-20T12:00:00Z
status: human_needed
score: 5/5 must-haves verified
covered_files:
  - src/Cafe.Agent.Core/AgentController.cs
  - src/Cafe.Agent/KeyboardBlocker.cs
  - src/Cafe.Agent/WpfLockScreen.cs
  - src/Cafe.AgentSim/SimFleet.cs
  - src/Cafe.Server/Agents/AgentCommandService.cs
  - src/Cafe.Server/Agents/PcLockExpiryActionHandler.cs
covered_digest: "v2:sha256:97410764addf67562654bc77a04279df8a5e4af92f94bcbef283d7a0c7046a8b"
behavior_unverified: 0
overrides_applied: 0
human_verification:
  - test: "On a Windows PC, run Cafe.Agent.exe with no session."
    expected: "A full-screen borderless topmost lock window with cafe name, station name, reason and clock. Alt+Tab, Win (L/R), Alt+Esc, Ctrl+Esc, Alt+F4 and Ctrl+Shift+Esc do nothing; the window cannot be closed or found in the taskbar."
    why_human: "WPF window plus WH_KEYBOARD_LL hook only run on Windows. Chord logic (KeyBlockPolicy) is unit-tested, the hook is build-verified only."
  - test: "On a dual-monitor Windows PC, lock the PC, then plug and unplug a monitor while locked."
    expected: "Every screen is covered, keyboard focus stays on a lock window with no activation flicker (CR-01 fix), and windows are rebuilt on monitor change."
    why_human: "Multi-monitor activation behaviour cannot run off Windows."
  - test: "Press Ctrl+Alt+Shift+F12 on the lock screen; enter wrong codes, kill and relaunch the agent, enter correct code and choose Exit agent."
    expected: "Lockout (30 s, escalating to 15 min) survives relaunch; correct code unlocks; exit does not freeze the UI (CR-02 fix); codes are never logged."
    why_human: "WPF dialog and process lifecycle on Windows."
  - test: "Start a session from the dashboard for a real Windows PC, then run it through the 10, 5 and 1 minute warnings, expiry and grace."
    expected: "PC unlocks on start; a non-focus-stealing toast bottom-right of the primary screen shows the countdown, turns red in grace with 'Time is up - save your work'; PC locks after grace."
    why_human: "WS_EX_NOACTIVATE toast and real countdown rendering on Windows."
  - test: "Launch a second agent instance, and pre-create the agent mutex from another process."
    expected: "Second instance exits (code 3) only if the first answers a ping; otherwise it takes over and shows its own lock screen (WR-08 fail-open fix). Windows shutdown/logoff is never blocked."
    why_human: "Named mutex and events and OS session-ending behaviour are Windows-only."
  - test: "Send Lock, Unlock, Message, Shutdown, Restart from the station PC menu to a real PC; pull the network cable while a session runs."
    expected: "Commands take effect; PC stays locked when server unreachable, keeps counting down locally, and reconnects with backoff; dashboard flips online/offline."
    why_human: "Real shutdown.exe (full path, WR-09), real network and browser dashboard."
  - test: "In a browser, open /settings with two agents using the same machine id."
    expected: "A 'Machine ID conflicts' alert appears and the first connection is kept (WR-03)."
    why_human: "The conflict alert has no UI test."
---

# Phase 3: PC Agent Verification Report

**Phase Goal:** Gaming PCs lock and unlock themselves based on session state and staff can control them from the desk (ROADMAP wording). Caller wording: PCs lock/unlock automatically from session state, show countdown warnings, accept remote commands, and the whole flow can be tested without Windows.
**Verified:** 2026-11-20
**Status:** human_needed (all platform-neutral checks pass; Windows-only behaviours remain)
**Re-verification:** No previous 03-VERIFICATION.md existed (initial verification). No build or test was run in this pass; evidence is code reading plus `dotnet test Cafe.sln` 1054/1054 passed at HEAD~2.

## Goal Achievement

### Observable Truths (ROADMAP Success Criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | PC agent registers by machine ID and shows online/offline on the dashboard | VERIFIED | `AgentHub` + `AgentAuthMiddleware` (constant-time `FixedTimeEquals`, hub mapped `AllowAnonymous` at `Program.cs:143`, middleware at l.128). `AgentRegistry` tracks machine id and raises `Changed`; station status Offline/Idle from registry; `AgentsPanel.razor` lists connected and unregistered agents. Tests: `AgentHubAuthTests`, `AgentRegistryTests`, `AgentStatusTests`, `AgentTracerTests`. |
| 2 | No session: full-screen lock overlay that Alt+Tab, Win, Alt+F4, Task Manager cannot escape | VERIFIED in code; Windows behaviour is human | `LockWindow` (Topmost, ShowInTaskbar=false, Closing cancelled), one per monitor in `WpfLockScreen`; `KeyboardBlocker` installs WH_KEYBOARD_LL and calls `KeyBlockPolicy.ShouldBlock` (Win, Alt+Tab, Alt+Esc, Ctrl+Esc, Alt+F4, Ctrl+Shift+Esc; emergency chord never blocked); `KeyBlockPolicyTests`. Hook install failure is logged and retried (IN-04). |
| 3 | Start unlocks; stop/expire (after grace) locks; warnings at 10/5/1 and during grace | VERIFIED | `AgentCommandService` maps start/resume=Unlock, pause/stop=Lock, extend=SessionUpdated, move=Lock old + Unlock new, state-driven under a per-machine semaphore with sequence numbers. `PcLockExpiryActionHandler` (registered in `AgentServiceCollectionExtensions`) locks after grace and throws when the agent is offline so the evaluator retries (WR-01). Warning and grace pushes via `ShowWarning` / `GraceStarted`; agent local countdown in `AgentController`. Tests: `AgentSessionMappingTests`, `AgentLifecycleIntegrationTests` (real controller against real server through all stages), `AgentCommandOrderingTests`, `PcLockExpiryActionHandlerTests`. |
| 4 | Staff can send lock, unlock, message, shutdown, restart from the dashboard | VERIFIED | `PcCommandMenu.razor` (used in `StationCard`) calls `AgentRemoteControlService.SendAsync`; confirmations for unlock/shutdown/restart, 1-500 char message, offline refusal; every command logged in `AgentCommandLog` (migration `20261119214900_AddAgentCommandLog`), shown on `/sessions`. Agent executes via `IPowerControl` (`PowerControl.cs`, shutdown.exe by full system path). `AgentRemoteControlTests`. |
| 5 | Server unreachable: agent stays locked and reconnects; simulator runs N fake agents | VERIFIED | Agent starts Locked, `AgentRetryPolicy` backoff 0, 2, 5, 10, 30 s then every 30 s forever, stable-connection reset (WR-03), monotonic-clock countdown (WR-04). `Cafe.AgentSim` (`SimFleet`, `SimOptions` `--server --key --count --prefix`, `SimAgentDevices`). Tests: `AgentConnectionResilienceTests`, `AgentControllerStateTests`, `AgentSimTests`. |

**Score:** 5/5 truths verified. Windows-only parts are routed to human verification, not counted as failures.

### 03-REVIEW-FIX.md fixes confirmed present in HEAD

All 13 fix commits are ancestors of HEAD (`567f06a 18b4d4a 767fd61 591ace2 cda2352 006dd00 561421c 7b895a2 40976f0 48c392d 08eef85 6fd775e 7c99900`). Code evidence: `ConnectedLockSlack` self-lock (AgentController l.430-433, WR-01); `Sequence` fields in `AgentDtos` (WR-02); `AgentRegistry.Register` returns false on duplicates plus conflict panel (WR-03); `MonotonicMilliseconds` on `IAgentClock` (WR-04); NOCASE collate lookup (WR-05); try/catch per `Changed` subscriber (WR-06); `EmergencyAttemptTracker` in Agent.Core with persisted escalating lockout (WR-07); `SingleInstance` ping/pong takeover (WR-08); `Environment.SystemDirectory` shutdown path (WR-09); stale SessionUpdated ignored while Locked (IN-02); hook-failure logging (IN-04). Primary-window-only reactivation (CR-01) and non-blocking `ExitAgent` (CR-02) are in `LockWindow` / `AgentApplication`; build-verified only, see human items.

### Required Artifacts

| Artifact | Status | Details |
|----------|--------|---------|
| `Cafe.Agent.Core` (AgentController 607 lines, KeyBlockPolicy, EmergencyCodeHasher, AgentSettingsLoader, retry policy) | VERIFIED | Platform-neutral, unit-tested. |
| `Cafe.Agent` (net10.0-windows WPF, EnableWindowsTargeting, LockWindow, WarningToast, KeyboardBlocker, SingleInstance) | VERIFIED (build only) | Part of `Cafe.sln`; cannot run on macOS. |
| `Cafe.AgentSim` | VERIFIED | `SimFleet`, `SimOptions`, tests. |
| `Cafe.Server/Agents/*` (Hub, Registry, CommandService, RemoteControl, AdminService, SettingsService, handler) | VERIFIED | DI via `AddAgentServices` (Program.cs:79). |
| README "Running the PC agent" | VERIFIED | `README.md:31`. |

### Key Link Verification

| From | To | Status |
|------|----|--------|
| SessionService signals to `ISessionEventSink` (`AgentCommandService`) to hub clients | WIRED |
| ExpiryEvaluator to `PcLockExpiryActionHandler` to agent Lock | WIRED |
| AgentRegistry.Changed to Home dashboard refresh | WIRED |
| StationCard to `PcCommandMenu` to `AgentRemoteControlService` to hub | WIRED |
| AgentController to `ILockScreen` / `IWarningDisplay` / `IPowerControl` (WPF impls) | WIRED |
| KeyboardBlocker to `KeyBlockPolicy` | WIRED |

### Data-Flow Trace (Level 4)

Session state flows from DB through `AgentCommandService` (state re-read under lock) to the SignalR client to `AgentController` state to the lock/warning UI; agent connection state flows registry to station status to dashboard. FLOWING.

### Behavioral Spot-Checks

| Behavior | Evidence | Status |
|----------|----------|--------|
| Full suite incl. agent lifecycle integration | 1054/1054 at HEAD~2 (supplied; not re-run) | PASS |

### Probe Execution

No probes declared. Skipped.

### Requirements Coverage

| Requirement | Source Plan(s) | Status | Evidence |
|-------------|---------------|--------|----------|
| AGNT-01 | 03-01, 03-02, 03-04 | SATISFIED | hub, registry, status |
| AGNT-02 | 03-01, 03-03, 03-04, 03-05 | SATISFIED in code (Windows behaviour human) | lock screen, KeyBlockPolicy, hook |
| AGNT-03 | 03-01..03-04 | SATISFIED | command mapping, lifecycle test |
| AGNT-04 | 03-02, 03-03, 03-04, 03-05 | SATISFIED (toast rendering human) | ShowWarning, GraceStarted, local countdown |
| AGNT-05 | 03-04, 03-05 | SATISFIED | PcCommandMenu, RemoteControl |
| AGNT-06 | 03-01, 03-03, 03-04, 03-05 | SATISFIED | locked-by-default, backoff, re-push |
| AGNT-07 | 03-03 | SATISFIED | AgentSim |

All 7 IDs are accounted for in plan frontmatter; no orphans.

### Anti-Patterns Found

None. No TODO/FIXME/TBD/XXX in Cafe.Agent, Cafe.Agent.Core, Cafe.AgentSim or Server/Agents.

### Human Verification Required

See frontmatter (Windows lock screen and keyboard hook, multi-monitor, emergency unlock, warning toast, single-instance, real commands and network loss, conflict alert).

### Gaps Summary

No code gaps. Documentation drift (WARNING, not goal-blocking): `.planning/REQUIREMENTS.md` still shows AGNT-01, 02, 03, 04, 06, 07 as unchecked and "Pending" in the traceability table (only AGNT-05 is Complete). These should be marked Complete, or left Pending until the Windows human checks pass. Known residual risks accepted in the fix report: the 20 s self-lock slack can end grace early if GraceStarted is lost; staff Lock/Unlock are unsequenced; a Phase 7 watchdog/installer must restart the agent and set file ACLs.

---

_Verified: 2026-11-20_
_Verifier: Claude (gsd-verifier)_
