---
phase: 03-pc-agent
reviewed: 2026-11-20T00:00:00Z
depth: standard
files_reviewed: 52
files_reviewed_list:
  - src/Cafe.Agent.Core/Abstractions.cs
  - src/Cafe.Agent.Core/AgentCommandLine.cs
  - src/Cafe.Agent.Core/AgentConnectionOptions.cs
  - src/Cafe.Agent.Core/AgentController.cs
  - src/Cafe.Agent.Core/AgentRetryPolicy.cs
  - src/Cafe.Agent.Core/AgentSettings.cs
  - src/Cafe.Agent.Core/AgentSettingsLoader.cs
  - src/Cafe.Agent.Core/AgentState.cs
  - src/Cafe.Agent.Core/EmergencyCodeHasher.cs
  - src/Cafe.Agent.Core/KeyBlockPolicy.cs
  - src/Cafe.Agent.Core/SystemAgentClock.cs
  - src/Cafe.Agent/AgentApplication.cs
  - src/Cafe.Agent/EmergencyUnlockDialog.cs
  - src/Cafe.Agent/FileLoggerProvider.cs
  - src/Cafe.Agent/KeyboardBlocker.cs
  - src/Cafe.Agent/LockWindow.cs
  - src/Cafe.Agent/Native/MonitorEnumerator.cs
  - src/Cafe.Agent/Native/NativeMethods.cs
  - src/Cafe.Agent/PowerControl.cs
  - src/Cafe.Agent/Program.cs
  - src/Cafe.Agent/SingleInstance.cs
  - src/Cafe.Agent/WarningToast.cs
  - src/Cafe.Agent/WpfLockScreen.cs
  - src/Cafe.Agent/WpfWarningDisplay.cs
  - src/Cafe.AgentSim/Program.cs
  - src/Cafe.AgentSim/SimOptions.cs
  - src/Cafe.Server/Agents/AgentAdminService.cs
  - src/Cafe.Server/Agents/AgentAuthMiddleware.cs
  - src/Cafe.Server/Agents/AgentCommandLogService.cs
  - src/Cafe.Server/Agents/AgentCommandService.cs
  - src/Cafe.Server/Agents/AgentHub.cs
  - src/Cafe.Server/Agents/AgentRegistry.cs
  - src/Cafe.Server/Agents/AgentRemoteControlService.cs
  - src/Cafe.Server/Agents/AgentServiceCollectionExtensions.cs
  - src/Cafe.Server/Agents/AgentSettingsService.cs
  - src/Cafe.Server/Agents/PcLockExpiryActionHandler.cs
  - src/Cafe.Server/Auth/SetupGateMiddleware.cs
  - src/Cafe.Server/Components/Pages/Home.razor
  - src/Cafe.Server/Components/Pages/Sessions.razor
  - src/Cafe.Server/Components/Pages/Settings.razor
  - src/Cafe.Server/Components/Shared/AgentsPanel.razor
  - src/Cafe.Server/Components/Shared/PcCommandMenu.razor
  - src/Cafe.Server/Components/Shared/StationCard.razor
  - src/Cafe.Server/Dashboard/DashboardService.cs
  - src/Cafe.Server/Dashboard/DefaultStationStatusProvider.cs
  - src/Cafe.Server/Data/AgentCommandLog.cs
  - src/Cafe.Server/Program.cs
  - src/Cafe.Server/Sessions/ExpiryEvaluator.cs
  - src/Cafe.Server/Sessions/ISessionEventSink.cs
  - src/Cafe.Server/Sessions/SessionService.cs
  - src/Cafe.Server/Sessions/SessionSignalPublisher.cs
  - src/Cafe.Shared/Agents/AgentProtocol.cs
  - src/Cafe.Shared/Agents/IAgentClient.cs
findings:
  critical: 2
  warning: 9
  info: 7
  total: 18
status: issues_found
---

# Phase 3: Code Review Report

**Reviewed:** 2026-11-20
**Depth:** standard
**Files Reviewed:** 52 source files (tests, EF migrations and csproj/json only skimmed or skipped)
**Status:** issues_found

## Summary

Server-side hub authentication is solid: the key is compared in constant time over SHA-256 digests, it is never logged, the hub is exempt from cookie auth but gated by `AgentAuthMiddleware` (which is registered before `UseAuthentication`), and `Unregister` correctly ignores late disconnects of a replaced connection. The command audit log, `Dispose` of registry subscriptions in `Home.razor` and `AgentsPanel.razor`, and the pure `KeyBlockPolicy` look correct.

The WPF agent has two defects that I consider ship-blockers: a focus ping-pong between lock windows on multi-monitor PCs, and a sync-over-async deadlock on the emergency "exit agent" path. The remaining concerns are mostly fail-secure gaps: swallowed send failures with no retry, unordered concurrent command delivery, and wall-clock-based offline countdown.

## Critical Issues

### CR-01: Lock windows on multi-monitor PCs fight for activation in an endless loop

**File:** `src/Cafe.Agent/LockWindow.cs:117-132` (with `WpfLockScreen.cs:105-118`)
**Issue:** Every `LockWindow` re-activates itself in `OnDeactivated`. With two or more monitors there are two or more windows. When window B becomes active, window A fires `Deactivated`, queues `Activate()`, which deactivates B, whose handler queues `Activate()` on B, and so on. This is an unbounded dispatcher ping-pong (100% UI thread, flicker, and the UI thread is too busy for the emergency chord or dialog to be reliably handled). `SuppressReactivation` only protects the emergency dialog. Gaming cafes commonly have dual-monitor PCs, so this is a normal configuration.
**Fix:** Do not re-activate when the new foreground window belongs to this process, and only the primary window should hold focus:
```csharp
private void OnDeactivated(object? sender, EventArgs e)
{
    if (_allowClose || AgentApplication.IsExiting || SuppressReactivation) return;
    Dispatcher.BeginInvoke(() =>
    {
        if (_allowClose || AgentApplication.IsExiting || SuppressReactivation) return;
        if (Application.Current.Windows.OfType<LockWindow>().Any(w => w.IsActive)) return; // another lock window has focus
        Topmost = true;
        Activate();
    });
}
```
Also consider `SWP_NOACTIVATE` for secondary monitor windows in `OnSourceInitialized`.

### CR-02: Emergency unlock with "Also exit the agent" deadlocks the UI thread and leaves the PC locked

**File:** `src/Cafe.Agent/AgentApplication.cs:112-132` (calls `AgentController.DisposeAsync`, `AgentController.cs:476-516`)
**Issue:** `ExitAgent` runs on the WPF UI thread and does `_controller.DisposeAsync().AsTask().GetAwaiter().GetResult()`. `DisposeAsync` awaits `connection.DisposeAsync()` and `loop.WaitAsync(5s)` without `ConfigureAwait(false)`; the first genuinely asynchronous await captures the `DispatcherSynchronizationContext` whose thread is blocked in `GetResult()`. Classic deadlock. Because `OnEmergencyUnlock` called `EmergencyUnlock()` first, which only queues `Hide` via `BeginInvoke`, the Hide never runs: the lock screen stays up, the UI is frozen, and the emergency unlock the operator just authenticated for does not work.
**Fix:** Add `.ConfigureAwait(false)` to every await in `AgentController.DisposeAsync`, and/or hide the lock screen before disposing, and never block the UI thread:
```csharp
_lockScreen?.Hide(); _warnings?.Hide();
_ = Task.Run(async () => { await _controller!.DisposeAsync(); await Dispatcher.InvokeAsync(Shutdown); });
```

## Warnings

### WR-01: A lost Lock command is never retried, and a connected agent never self-locks at grace end

**File:** `src/Cafe.Server/Agents/AgentCommandService.cs:262-272`, `PcLockExpiryActionHandler.cs:6-17`, `src/Cafe.Agent.Core/AgentController.cs:230-249`
**Issue:** `SendAsync` swallows every failure (timeout included), so `LockStationAsync` and `PcLockExpiryActionHandler.ExecuteAsync` always "succeed". The expiry evaluator then records `ExpiryActionFired` and never retries, contradicting the handler's own doc comment ("at-least-once delivery"). On the agent, `Tick` only locks at zero when `!_connected`. So if the expiry Lock is dropped on a connection that stays up (stalled send, 5 s timeout), the PC stays unlocked indefinitely after the session ended. This is the product's core value ("reliably stops being usable").
**Fix:** Have `LockStationAsync` return/throw on delivery failure so the evaluator's retry/`ExpiryActionFailed` path runs; and in `Tick`, lock locally when grace has ended (and remaining reached 0 with no grace) even while connected, with the server remaining authoritative for extensions (an Unlock/SessionUpdated arrives before zero in the normal case).

### WR-02: Concurrent session signals can be delivered out of order, leaving the PC in the wrong state

**File:** `src/Cafe.Server/Agents/AgentCommandService.cs:96-107, 224-240`
**Issue:** `Paused` and `Stopped` send an unconditional `Lock` after an independent DB lookup; `Resumed`/`Started` re-read the session and send `Unlock`. Signals from different request threads (pause then immediate resume, stop then immediate new session on the same station, extend racing with pause) are not serialized per station, so the `Lock` can land after the `Unlock` (PC locked during an active paid session) or the `Unlock` after the `Lock` (PC open after pause/stop). `SessionUpdated` in the agent also unlocks a Locked PC (`AgentController.cs:157-162`), widening the window.
**Fix:** Serialize delivery per machine (a per-machine `SemaphoreSlim`), and have each handler compute the command from the current DB state at send time (reuse `PushStateAsync` logic) instead of acting on the signal kind alone.

### WR-03: Any holder of the shared key can displace a real agent, and duplicate machine ids cause an unbounded zero-delay reconnect storm

**File:** `src/Cafe.Server/Agents/AgentRegistry.cs:24-45`, `src/Cafe.Agent.Core/AgentController.cs:395-412`
**Issue:** "Newest connection wins" with the machine id taken from an unauthenticated-beyond-shared-key header means a second PC (cloned image with the same `MachineId`, or a rogue client with the key) evicts the legitimate agent and receives its Unlock commands. The evicted agent sees `Closed`, and `OnClosedAsync` starts a fresh `ConnectLoopAsync` with `attempt = 0`, whose delay is `TimeSpan.Zero`, so it immediately evicts the other one; the two ping-pong at network speed forever and the dashboard flaps.
**Fix:** Keep a monotonically growing backoff across `Closed` (reset only after a connection has been stable, e.g. 30 s), and log a warning server-side when a live connection is replaced. Optionally refuse a replacement while the old connection is healthy and from a different remote IP.

### WR-04: Offline countdown uses the wall clock, so setting the clock back extends a session

**File:** `src/Cafe.Agent.Core/AgentController.cs:372-387, 403-405`, `SystemAgentClock.cs`
**Issue:** `SecondsSinceReceipt` and `GraceSecondsLeft` use `DateTimeOffset.UtcNow`. A customer who can change the PC date/time (or any local admin) while the network is unplugged can move time backwards (negative `SecondsSinceReceipt` is not clamped for remaining, unlike `ComputeElapsed`) and keep playing. The skew term cancels out and gives no protection.
**Fix:** Base the offline countdown on a monotonic clock (`Stopwatch.GetTimestamp()` captured when SessionInfo/GraceStarted arrives), and clamp the delta at >= 0.

### WR-05: Machine-id matching is case-insensitive in the registry but case-sensitive in the database

**File:** `src/Cafe.Server/Agents/AgentCommandService.cs:44` (also `ResolveAsync` and `AgentRemoteControlService`), vs `AgentRegistry.cs:16` and `AgentAdminService.cs:139`
**Issue:** `db.Stations.FirstOrDefaultAsync(s => s.AgentMachineId == machineId)` is an ordinal comparison in SQLite while the registry and the admin UI treat ids case-insensitively. A station whose stored id differs only in case (hand-edited, or an agent whose `MachineId` was retyped) shows as registered in the UI yet `PushStateAsync` finds no station and locks the PC forever with "This PC is not set up yet".
**Fix:** Normalize ids to upper-case (or use `EF.Functions.Collate`/`COLLATE NOCASE` on the column) in one place on write and compare consistently.

### WR-06: `AgentRegistry.Changed` subscribers can break hub connect and disconnect

**File:** `src/Cafe.Server/Agents/AgentRegistry.cs:47, 61`
**Issue:** `Changed?.Invoke(machineId)` runs synchronously inside `AgentHub.OnConnectedAsync`/`OnDisconnectedAsync` with no try/catch. A throwing handler (for example `InvokeAsync` on a just-disposed Blazor component, which throws synchronously, or `Home.OnAgentChanged` after `_cts` disposal) aborts the hub connection before `PushStateAsync` runs, and stops other subscribers from being notified.
**Fix:** Invoke each handler in `GetInvocationList()` inside its own try/catch and log failures.

### WR-07: Emergency unlock brute-force protection is weak

**File:** `src/Cafe.Agent/EmergencyUnlockDialog.cs:7-34`, `src/Cafe.Agent.Core/AgentCommandLine.cs:36-45`, `EmergencyCodeHasher.cs`
**Issue:** (a) The failure counter and lockout live only in process memory, so killing and relaunching the agent (Ctrl+Alt+Del, Task Manager) resets them. (b) No minimum code length or strength is enforced by `--hash-code`, so a 4-digit code can be tried at 6 per minute indefinitely, or offline against the hash in the readable `agentsettings.json` (200k PBKDF2 iterations is trivial for 10^4 candidates). (c) Failed attempts are not logged, so a guessing campaign leaves no trace; only success is logged.
**Fix:** Enforce a minimum length (e.g. 8 chars) in `AgentCommandLine`, persist lockout state (e.g. escalating lockout in a protected file), log each failure (never the code), and ACL `agentsettings.json` to administrators only in the setup docs.

### WR-08: Fail-open paths when the agent is not running

**File:** `src/Cafe.Agent/SingleInstance.cs:19-33`, `Program.cs:35-41`, `AgentApplication.cs:44-50`
**Issue:** (a) A global mutex with the default ACL means any process can pre-create `Global\CafeManagerAgent`; the real agent then logs and exits with code 3, showing no lock screen at all. (b) Only `DispatcherUnhandledException` is handled; an unhandled exception on a SignalR/thread-pool thread terminates the process and the PC is left unlocked with nothing restarting it.
**Fix:** On a not-first instance, fail secure (show the lock screen or at least retry for the mutex rather than exiting), register `AppDomain.UnhandledException`/`TaskScheduler.UnobservedTaskException` loggers, and document/provision a watchdog (scheduled task restart on failure) as part of setup.

### WR-09: `shutdown.exe` is resolved without a full path

**File:** `src/Cafe.Agent/PowerControl.cs:17`
**Issue:** `new ProcessStartInfo("shutdown.exe", ...)` with `UseShellExecute = false` searches the application directory before System32 (CreateProcess search order). If the agent folder is writable by the gaming user, planting `shutdown.exe` there gives code execution as whatever account runs the agent.
**Fix:** `Path.Combine(Environment.SystemDirectory, "shutdown.exe")`.

## Info

### IN-01: Shared key accepted from the query string on every hub request, over plain HTTP

**File:** `src/Cafe.Server/Agents/AgentAuthMiddleware.cs:21-25`, `AgentSettingsLoader.cs:63`
**Issue:** The `access_token` query fallback applies to all transports and any HTTP request logging that records the query string will capture the key; the agent also accepts `http://` URLs, sending the key in cleartext on the LAN. Acceptable for a local-only install but worth documenting; restrict the query fallback to WebSocket upgrade requests.

### IN-02: Stale `SessionUpdated` unlocks a locked PC

**File:** `src/Cafe.Agent.Core/AgentController.cs:157-162`
**Issue:** A `SessionUpdated` arriving after a `Lock` (see WR-02) flips Locked to Unlocked. Prefer requiring an explicit `Unlock` for state escalation, or carry a sequence number.

### IN-03: Controller events and display calls run while holding `_gate`

**File:** `src/Cafe.Agent.Core/AgentController.cs:146-160, 337-345`
**Issue:** `StateChanged`/`ConnectedChanged` and `ILockScreen`/`IWarningDisplay` callbacks are invoked under the lock. Today they only `BeginInvoke`, so no deadlock, but a future synchronous subscriber that calls back into the controller from the UI thread would deadlock. Raise events outside the lock.

### IN-04: Unused P/Invoke constants and low-level hook failure is silent

**File:** `src/Cafe.Agent/Native/NativeMethods.cs:14-24`, `KeyboardBlocker.cs:27`
**Issue:** `SWP_NOACTIVATE` and `SWP_SHOWWINDOW` partly unused; `SetWindowsHookEx` returning 0 is not logged, so a failed install leaves the PC with an unblocked keyboard without any trace. Also Windows silently removes a low-level hook if the callback exceeds the timeout; consider re-installing on each `Show`.

### IN-05: No DPI manifest

**File:** `src/Cafe.Agent/Cafe.Agent.csproj`
**Issue:** The comment in `LockWindow` says positions are physical pixels, but without a PerMonitorV2 manifest EnumDisplayMonitors returns DPI-virtualized rectangles on mixed-DPI setups, which can leave uncovered edges. Add an `app.manifest` with `dpiAwareness` PerMonitorV2.

### IN-06: Starting a session blocks on agent delivery

**File:** `src/Cafe.Server/Sessions/SessionService.cs:107, 431`, `AgentCommandService.cs:262`
**Issue:** Signals are published inline after the commit, so a stalled agent adds up to 5 s per sink to the staff's Start/Pause/Stop click. Fire-and-forget through a bounded channel would keep the UI responsive (and is the natural place to serialize per machine, WR-02).

### IN-07: Minor polish

**File:** `src/Cafe.Server/Agents/AgentRemoteControlService.cs:149`, `EmergencyUnlockDialog.cs:136-153`, `AgentCommandLine.cs`
**Issue:** `Truncate` can split a surrogate pair; the lockout status text "Too many wrong codes" is not cleared when the lockout ends; `--hash-code <code>` exposes the secret in shell history and the process list (offer a prompt or stdin mode).

---

_Reviewed: 2026-11-20_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
