---
phase: 03-pc-agent
fixed_at: 2026-11-20T04:10:00+05:30
review_path: /Users/dylenquyn/Cafe/.planning/phases/03-pc-agent/03-REVIEW.md
iteration: 1
findings_in_scope: 13
fixed: 13
skipped: 0
status: all_fixed
---

# Phase 3: Code Review Fix Report

**Fixed at:** 2026-11-20
**Source review:** .planning/phases/03-pc-agent/03-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 13 (CR-01, CR-02, WR-01..WR-09, IN-02, IN-04; IN-01, IN-03, IN-05, IN-06, IN-07 were out of scope)
- Fixed: 13
- Skipped: 0

**Verification environment:** the main checkout on branch `main` (the caller asked for commits on main, and no worktree was created, so there was no fast-forward or cleanup tail). `dotnet build Cafe.sln` gives 0 warnings and 0 errors. `dotnet test Cafe.sln` passes 478 of 478 (it was 465 before this run). The WPF project (`Cafe.Agent`) builds on macOS, but none of its code can run here. The WPF-only fixes (CR-01, CR-02, WR-07 dialog wiring, WR-08, WR-09, IN-04) are build-verified only and need a manual check on Windows.

Items marked "requires human verification" below are logic changes whose tests cover the platform-neutral part only.

## Fixed Issues

### CR-01: Lock windows fight for activation on multi-monitor PCs
**Files modified:** `src/Cafe.Agent/LockWindow.cs`, `src/Cafe.Agent/WpfLockScreen.cs`
**Commit:** 567f06a
**Applied fix:** Only the primary window (the first monitor, `IsPrimary`) re-activates itself on `Deactivated`. Secondary windows never do, and the primary also skips re-activation when another `LockWindow` is already active. The ping-pong is gone and the keyboard focus always stays on a lock window.
**Status:** fixed, requires human verification on a dual-monitor Windows PC.

### CR-02: Emergency "exit agent" deadlocks the UI thread
**Files modified:** `src/Cafe.Agent/AgentApplication.cs`, `src/Cafe.Agent.Core/AgentController.cs`
**Commit:** 18b4d4a
**Applied fix:** `ExitAgent` now hides the lock screen and warnings first (queued on the dispatcher). It then disposes the controller on a `Task.Run` with `ConfigureAwait(false)` and finally `Dispatcher.InvokeAsync`s `Shutdown`. Nothing blocks the UI thread any more. `AgentController.DisposeAsync` uses `ConfigureAwait(false)` on all of its awaits.
**Status:** fixed, requires human verification on Windows.

### WR-01: A lost Lock command is never retried, and a connected agent never self-locks
**Files modified:** `src/Cafe.Server/Agents/PcLockExpiryActionHandler.cs`, `src/Cafe.Server/Agents/AgentCommandService.cs`, `src/Cafe.Agent.Core/AgentController.cs`, `src/Cafe.Agent.Core/AgentConnectionOptions.cs`, `tests/Cafe.Tests/PcLockExpiryActionHandlerTests.cs`, `tests/Cafe.Tests/AgentControllerStateTests.cs`
**Commit:** 767fd61
**Applied fix:**
- `SendAsync` now returns whether the message was delivered. The new `LockExpiredStationAsync` returns false when the agent is offline or the send fails or times out, and the handler throws. The evaluator's at-least-once retry (5 attempts, 15 s apart) now runs, and after that the on-connect state push still locks the PC.
- Non-PC stations, and PC stations with no machine id, stay a quiet no-op.
- Decision (not in the review): the retried expiry lock does nothing when a newer non-ended session exists on the station. Without this, making failures retry would let a retry lock a PC that staff had already restarted.
- Agent `Tick`: a connected agent now locks itself when the server has been silent for `ConnectedLockSlack` (default 20 s) past zero remaining, or past the end of grace. The slack stops a flicker when the server's extension or Lock arrives just after the local zero.
**Status:** fixed, requires human verification. The slack is a judgement call. A GraceStarted message lost for more than 20 s now ends the grace early.

### WR-02: Concurrent session signals delivered out of order
**Files modified:** `src/Cafe.Server/Agents/AgentCommandService.cs`, `src/Cafe.Shared/Agents/AgentDtos.cs`, `src/Cafe.Agent.Core/AgentController.cs`, `tests/Cafe.Tests/AgentCommandOrderingTests.cs`
**Commit:** 591ace2
**Applied fix:**
- Every flow for one machine id holds a per-machine `SemaphoreSlim`.
- Started, Resumed, Paused, Stopped and Moved are now state driven. They re-read the database under the lock and share the `PushStateCoreAsync` logic. Stopped and Moved-from only supply the lock reason text (`EndedReason`, `Session moved to X`). The ShowWarning that `PushStateAsync` sends for a session already inside a warning window is sent only on connect and admin pushes, never on Start, Resume or Move.
- `LockInfo` and `SessionInfo` gain `long Sequence = 0`. This is wire-compatible, because older agents ignore the extra field and 0 means unsequenced. The server counter is seeded from the clock in milliseconds times 1000, so it keeps growing across restarts.
- The agent ignores a sequenced Lock, Unlock or SessionUpdated that is not newer than the last one it applied. It resets the counter on disconnect.
- Limitation: commands from `AgentRemoteControlService` (staff Lock and Unlock) stay unsequenced (0) and are always applied. They are not serialized through the per-machine lock either.
**Status:** fixed, requires human verification (logic change).

### WR-03: Duplicate machine ids displace the live agent and cause a reconnect storm
**Files modified:** `src/Cafe.Server/Agents/AgentRegistry.cs`, `src/Cafe.Server/Agents/AgentHub.cs`, `src/Cafe.Server/Agents/AgentAdminService.cs`, `src/Cafe.Server/Components/Shared/AgentsPanel.razor`, `src/Cafe.Agent.Core/AgentController.cs`, `src/Cafe.Agent.Core/AgentConnectionOptions.cs`, `tests/Cafe.Tests/AgentHubAuthTests.cs`
**Commit:** cda2352
**Applied fix:**
- `AgentRegistry.Register` now returns false for a machine id that is already connected. It records an `AgentConflictInfo` (attempt count, remote address, last time), and the hub logs a warning and aborts the newcomer. The first connection is kept.
- `/settings` shows a "Machine ID conflicts" alert. The conflict clears when the live connection ends or a registration succeeds.
- Agent: the reconnect attempt index is now a field that grows across `Closed` events. It resets only after a connection stayed up for `StableConnectionThreshold` (30 s). The old test "Newest connection wins" was replaced by tests for the new behaviour, including a backoff test (a zero-delay storm would be dozens of attempts, the test asserts 2 to 4).
- Decision: a legitimate agent that reconnects before the server has noticed its old half-open connection is refused until the SignalR keep-alive timeout (about 30 s by default) drops the old one. Its backoff then lets it in.
**Status:** fixed, requires human verification. The new conflict alert has no UI test.

### WR-04: Offline countdown uses the wall clock
**Files modified:** `src/Cafe.Agent.Core/Abstractions.cs`, `src/Cafe.Agent.Core/SystemAgentClock.cs`, `src/Cafe.Agent.Core/AgentController.cs`, `tests/Cafe.Tests/Infrastructure/AgentUnitFakes.cs`, `tests/Cafe.Tests/AgentLifecycleIntegrationTests.cs`, `tests/Cafe.Tests/AgentControllerStateTests.cs`
**Commit:** 006dd00
**Applied fix:** `IAgentClock` gains `MonotonicMilliseconds` (`Environment.TickCount64` in `SystemAgentClock`). The seconds since the last server message and the grace end are now measured on the monotonic clock, and the delta is never negative. The wall clock is used only for the skew estimate (`EstimatedServerNow`). Three tests cover setting the wall clock back and forward and the grace countdown.
**Status:** fixed.

### WR-05: Machine-id matching case-insensitive in registry but case-sensitive in DB
**Files modified:** `src/Cafe.Server/Agents/AgentCommandService.cs`, `tests/Cafe.Tests/AgentMachineIdCaseTests.cs`
**Commit:** 561421c
**Applied fix:** The station lookup in the state push now uses `EF.Functions.Collate(..., "NOCASE")`, the same collation as the unique index. Note: the `AgentMachineId` column is already declared NOCASE, so SQLite compared it case-insensitively and the added test also passes without the change. The change makes the intent explicit, and the test pins the behaviour. `AgentRemoteControlService` and `ResolveAsync` look stations up by id, so they need no change.
**Status:** fixed (defensive).

### WR-06: `AgentRegistry.Changed` subscribers can break hub connect and disconnect
**Files modified:** `src/Cafe.Server/Agents/AgentRegistry.cs`, `tests/Cafe.Tests/AgentRegistryTests.cs`
**Commit:** 7b895a2
**Applied fix:** `RaiseChanged` invokes each subscriber from `GetInvocationList()` in its own try/catch and logs failures (optional `ILogger<AgentRegistry>`). It runs outside the registry lock.
**Status:** fixed.

### WR-07: Emergency unlock brute-force protection is weak
**Files modified:** `src/Cafe.Agent.Core/EmergencyAttemptTracker.cs` (new, moved from the WPF project so it is testable), `src/Cafe.Agent.Core/AgentCommandLine.cs`, `src/Cafe.Agent/EmergencyUnlockDialog.cs`, `src/Cafe.Agent/WpfLockScreen.cs`, `tests/Cafe.Tests/EmergencyAttemptTrackerTests.cs`, `tests/Cafe.Tests/AgentCommandLineTests.cs`
**Commit:** 40976f0
**Applied fix:**
- The failure counter and lockout end persist to `%ProgramData%\CafeManager\agent\emergency-lockout.json` (atomic write). Killing and relaunching the agent no longer resets them.
- The lockout escalates: 30 s, 60 s, 2 min and so on, capped at 15 min.
- A corrupt state file fails secure with a lockout.
- Every failed attempt is logged as a warning, never the code.
- `--hash-code` requires at least 6 characters (`MinCodeLength`). The usage text says so and tells operators to restrict `agentsettings.json` to administrators.
- Documented: the ACLs on `agentsettings.json` and on the lockout file are set by the installer (Phase 7).
- Residual risk, not addressed: someone with write access to ProgramData can delete the state file, and a lockout is based on the wall clock.
**Status:** fixed, requires human verification of the dialog on Windows.

### WR-08: Fail-open paths when the agent is not running
**Files modified:** `src/Cafe.Agent/SingleInstance.cs`, `src/Cafe.Agent/Program.cs`, `src/Cafe.Agent/AgentApplication.cs`, `src/Cafe.Agent.Core/AgentController.cs`
**Commit:** 48c392d
**Applied fix:**
- The first instance answers a named-event ping, but only if its UI thread responds within 1 s. A second instance exits (code 3) only when it gets a pong within 2 s. Otherwise it logs a warning and takes over, shows its own lock screen and runs normally. A pre-created mutex or a hung agent can no longer leave the PC open.
- Added `AppDomain.UnhandledException` (logged as critical) and `TaskScheduler.UnobservedTaskException` (logged and observed) handlers. A fault on a non-UI thread still ends the process (.NET semantics), so the lock screen cannot be kept up in that case. The Phase 7 scheduled task must restart the agent on exit, and the agent locks again on start. The controller tick loop now catches and logs exceptions per tick, so it cannot die.
- Residual: another process could pre-create the ping and pong events as well. That needs local code execution and is out of scope.
**Status:** fixed, requires human verification on Windows. The Phase 7 watchdog is a dependency.

### WR-09: `shutdown.exe` resolved without a full path
**Files modified:** `src/Cafe.Agent/PowerControl.cs`
**Commit:** 08eef85
**Applied fix:** The process start uses `Path.Combine(Environment.SystemDirectory, "shutdown.exe")`.
**Status:** fixed.

### IN-02: Stale `SessionUpdated` unlocks a locked PC
**Files modified:** `src/Cafe.Agent.Core/AgentController.cs`, `tests/Cafe.Tests/AgentControllerStateTests.cs`
**Commit:** 6fd775e
**Applied fix:** `SessionUpdated` is ignored while the agent is Locked. Only `Unlock` opens a PC. The server already sends Unlock before SessionUpdated when extending from grace. Sequence numbers (WR-02) complete the fix.
**Status:** fixed.

### IN-04: Low-level hook failure is silent
**Files modified:** `src/Cafe.Agent/KeyboardBlocker.cs`, `src/Cafe.Agent/WpfLockScreen.cs`
**Commit:** 7c99900
**Applied fix:** `SetWindowsHookEx` returning 0 is now logged with the Win32 error, and `Install()` returns false. The lock windows stay topmost on every monitor. `ShowCore` retries `Install()` on each later Show or update. Not done: re-installing the hook after Windows silently removes it for a slow callback. The unused P/Invoke constants were left alone.
**Status:** fixed (build-verified).

---

_Fixed: 2026-11-20_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
