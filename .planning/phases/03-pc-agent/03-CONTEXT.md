# Phase 3: PC Agent — Context

**Source:** The approved project plan and the requirements Q&A, written 2026-11-20. The user asked for autonomous execution of all remaining phases, so Claude made the decisions marked (default).

## Phase Boundary

Gaming PCs lock and unlock from session state. The PC shows countdown warnings, staff can send remote commands (lock, unlock, message, shutdown, restart), and a simulator lets the whole flow be tested on macOS. The installer script is DEPL-02 in Phase 7; this phase only documents manual agent setup in the README.

## Existing Code to Build On
- Phase 2 session engine (`src/Cafe.Server/Sessions/`):
  - `SessionService` mutations and `SessionNotifier` change events.
  - `SessionTicker`/`ExpiryEvaluator`, which record WarningReached/Expired/GraceElapsed.
  - The `IExpiryActionHandler` seam: register an additional handler, keeping the logging one.
- `IStationStatusProvider` seam (Phase 1/2): Offline must now mean "a PC station whose agent is not connected".
- `Station.AgentMachineId`, unique and case-insensitive.
- `CafeWebApplicationFactory` for integration tests.
- Read the 02-*-SUMMARY.md files for the exact names that were built.

## Locked Decisions

### Projects
- **`src/Cafe.Shared`:** typed SignalR contracts.
  - `IAgentClient` (server→agent):
    - `Lock(LockInfo)` and `Unlock(SessionInfo)`.
    - `SessionUpdated(SessionInfo)`, `ShowWarning(int minutesRemaining)` and `GraceStarted(int graceSeconds)`.
    - `ShowMessage(string text)`, `Shutdown()` and `Restart()`.
  - DTOs: `SessionInfo` (Mode, remaining seconds or null for open-ended, elapsed seconds, station name, and a server UtcNow so the agent can correct for clock skew) and `LockInfo` (station name, cafe name, reason text).
  - Hub route constant `/hubs/agent`.
- **`src/Cafe.Agent.Core`:** a new `net10.0` class library with platform-neutral agent logic, so it can be tested on macOS.
  - `AgentController`: owns the HubConnection (`Microsoft.AspNetCore.SignalR.Client`), automatic reconnect with backoff (0, 2, 5, 10, 30s, then every 30s forever) and the state machine (Locked, Unlocked, Grace).
  - Abstractions: `ILockScreen` (Show/Hide), `IWarningDisplay` (ShowCountdown, ShowMessage, Hide), `IPowerControl` (Shutdown/Restart) and `IAgentClock`.
  - Local countdown: the agent ticks the remaining time every second between server updates, using server time to correct skew.
- **`src/Cafe.Agent`:** a new WPF app, `net10.0-windows`, `UseWPF`. Add `<EnableWindowsTargeting>true</EnableWindowsTargeting>` so `dotnet build Cafe.sln` works on macOS. It can't run on macOS, so its build is the only check here.
  - `LockWindow`: one per monitor (`System.Windows.Forms.Screen` via `UseWindowsForms`, or Win32 `EnumDisplayMonitors`), borderless, topmost, maximized, ShowInTaskbar=false. It shows the cafe name, station name, "Please see the front desk to start a session" and the clock.
  - `KeyboardBlocker`: `SetWindowsHookEx(WH_KEYBOARD_LL)`, active only while locked. It swallows Win (L/R), Alt+Tab, Alt+Esc, Ctrl+Esc and Alt+F4. Ctrl+Alt+Del can't be blocked by a hook; the installer adds the Task Manager policy in Phase 7.
  - `WarningToast`: a small topmost window at the bottom right showing the countdown and messages. It doesn't take focus (WS_EX_NOACTIVATE) so it never steals focus from games, and it goes red during Grace with "Time is up — save your work".
  - `PowerControl`: runs `shutdown.exe /s /t 0` or `/r /t 0`.
  - Config: `agentsettings.json` next to the exe with ServerUrl, SharedKey, MachineId (empty means `Environment.MachineName`) and EmergencyUnlockCodeHash.
  - Emergency unlock (default): Ctrl+Alt+Shift+F12 on the lock screen prompts for a code, checked against EmergencyUnlockCodeHash (PBKDF2 via `Rfc2898DeriveBytes.Pbkdf2`). This is for when the server is down. Ship a `--hash-code <code>` CLI switch that prints the hash for config.
  - Single instance (a named mutex); exit is only allowed after emergency unlock.
- **`src/Cafe.AgentSim`:** a new `net10.0` console app using Cafe.Agent.Core with console implementations of the abstractions.
  - Usage: `dotnet run --project src/Cafe.AgentSim -- --server http://localhost:5080 --key <key> --count 10 --prefix SIM-`.
  - It starts N agents with machine IDs `SIM-01..SIM-N` and logs every state transition in color, one line per event: `[SIM-03] UNLOCKED 60:00 remaining`.

### Server
- **`AgentHub`** (`src/Cafe.Server/Agents/AgentHub.cs`), mapped at `/hubs/agent` and typed `Hub<IAgentClient>`.
  - Authentication (default): a shared agent key, sent as the `X-Agent-Key` header (or the `access_token` query parameter for the WebSocket fallback), compared in constant time with `CryptographicOperations.FixedTimeEquals`.
  - The key is generated on first run (32 random bytes, base64url) and stored in the `Setting` table as `Agents:SharedKey`. It's shown on `/settings` with copy and regenerate buttons; regenerating disconnects all agents. The cookie-auth fallback policy must not apply to this hub.
  - The machine ID is the `X-Machine-Id` header.
- **`AgentRegistry`** singleton: machineId ↔ connectionId, last seen and agent version.
  - Matching a station is case-insensitive on `AgentMachineId`.
  - Unknown machine IDs that connect are listed on `/settings` under "Unregistered agents", with a button to create a station or assign one to an existing PC station.
  - Raises a change event so the dashboard updates the online/offline badge immediately.
- **`AgentCommandService`** maps session state to agent commands:
  - **On connect:** push the current state. Unlock with SessionInfo if the station has an Active session, a Grace message if it's in Grace, otherwise Lock.
  - **Session events:**
    - Start or resume: Unlock.
    - Pause: Lock with reason "Session paused". Paused time is free, so the PC locks.
    - Extend: SessionUpdated (plus Unlock if it was in Grace).
    - Move: Lock the old station and Unlock the new one.
    - Stop: Lock.
    - WarningReached: ShowWarning.
    - Expired → Grace: GraceStarted.
  - Subscribe to `SessionNotifier` and the ticker events. Prefer one explicit `ISessionEventSink` interface invoked by SessionService and the ticker over parsing notifier events.
- **`PcLockExpiryActionHandler`** (an `IExpiryActionHandler` for PC stations): Lock on GraceElapsed.
- **Manual commands** on the dashboard card for PC stations, in a small "PC" menu:
  - Lock, Unlock ("free unlock", no session; confirm first), Send message (text prompt), Shutdown and Restart (both confirmed).
  - Commands go through `AgentCommandService`, which is audited: add `AgentCommandIssued` to `SessionEventType`, or a separate `AgentCommandLog` if no session exists. Default: `AgentCommandLog` table with StationId, Command, AtUtc and Details, shown on /sessions under an "Agent commands" tab.
- **Status:** `IStationStatusProvider` treats a PC station with an AgentMachineId as Offline when the registry has no live connection. Session status still overrides it, and Offline shows as a small secondary indicator when a session is active on a disconnected PC.
- Starting a session on an offline PC is allowed, with a warning toast "PC agent offline — the PC will unlock when it reconnects."

### Testing
- `Cafe.Agent.Core` unit tests (in `tests/Cafe.Tests`, or a new `tests/Cafe.Agent.Tests` on net10.0):
  - State machine transitions.
  - Countdown ticking with a fake clock.
  - Lock by default when disconnected: AGNT-06 means the agent shows the lock screen when the connection drops and there's no active session. Default: if a session was active, the agent keeps counting down locally and locks at zero, so a server blip doesn't kick players out.
- Integration tests: start the server with `CafeWebApplicationFactory`, then connect a real `AgentController` through `HubConnectionBuilder().WithUrl(url, o => { o.HttpMessageHandlerFactory = _ => server.CreateHandler(); o.Transports = LongPolling; })`. Assert:
  - Lock arrives on connect.
  - Unlock on session start, Lock on stop, Lock on pause, ShowWarning on WarningReached (driven through FakeTimeProvider plus a direct evaluator call), and Lock after GraceElapsed.
  - A bad key is rejected.
  - An unknown machine ID appears in the registry's unregistered list.
- AgentSim: a smoke test that it builds; optionally a short in-process run against the test server.
- WPF project: `dotnet build` must succeed on macOS (EnableWindowsTargeting). Add a `README` section "Running the PC agent" covering config, the emergency unlock and the hash-code tool.

## Claude's Discretion
- Exact lock-screen visuals (dark, cafe-branded text, big clock).
- Logging framework: `Microsoft.Extensions.Logging` console/file.
- Whether the warning toast also plays a sound.

## Deferred
- Installer/scheduled task/Task Manager policy → Phase 7 (DEPL-02).
- Customer self-login at PCs → v2.

## Environment Notes
- Same as earlier phases: `export PATH="/usr/local/share/dotnet:$PATH"`, net10.0, Microsoft packages only (Microsoft.AspNetCore.SignalR.Client is fine), commits on main as dylenq with no attribution, and leave the untracked runtime files alone.
- Nothing Windows-only can be run here. Tests must not need Windows.
