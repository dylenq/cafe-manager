# Phase 4: Samsung TV Control — Context

**Source:** The approved project plan and the requirements Q&A, written 2026-11-20. The user asked for autonomous execution, so Claude made the decisions marked (default).

## Phase Boundary

Console stations' Samsung Tizen TVs:
- are paired from the app;
- power off automatically when a session expires (after grace);
- wake with Wake-on-LAN when a session starts;
- show reachability/power state on the dashboard, with manual on/off buttons.

There are no on-screen TV warnings; desk alerts from Phase 2 cover warnings. Real-TV verification needs the cafe hardware, so it's recorded as a human check.

## Existing Code to Build On
- `Station` already has `TvIpAddress`, `TvMacAddress` and `TvToken` (Phase 1).
- `IExpiryActionHandler` seam (Phase 2). Phase 3 added `PcLockExpiryActionHandler` and a session-event sink (`ISessionEventSink` or whatever Phase 3 built; read the 03-*-SUMMARY.md files). The TV handler hooks in the same way for Console stations.
- `IStationStatusProvider` seam, which Phase 3 updated to make agent connectivity drive Offline for PCs.

## Locked Decisions

### Driver (`src/Cafe.Server/Devices/`)
- `ITvDriver`:
  - `PairAsync(station, ct)`, which returns a token or an error.
  - `PowerOffAsync`, `PowerOnAsync` and `GetStateAsync`. The state is `TvPowerState { On, Standby, Unreachable, Unknown }`.
  - `SamsungTizenDriver` implements it.
- **Protocol:**
  - **Remote channel:** `wss://{ip}:8002/api/v2/channels/samsung.remote.control?name={base64("CafeManager")}&token={token}`. The TV uses a self-signed certificate, so accept any certificate **only** for this TV WebSocket client, via `ClientWebSocketOptions.RemoteCertificateValidationCallback`.
  - **Pairing:** wait up to 30s for the first `ms.channel.connect` event while the user accepts the prompt on the TV, then read `data.token`. Some models return no token; store an empty marker in that case. `ms.channel.unauthorized` (or a timeout) is a pairing failure with a clear message.
  - **Key send:** `{"method":"ms.remote.control","params":{"Cmd":"Click","DataOfCmd":"KEY_POWER","Option":"false","TypeOfRemote":"SendRemoteKey"}}`.
  - **Power state:** HTTP GET `http://{ip}:8001/api/v2/` with a 2s timeout. Read `device.PowerState` (`on`/`standby`) when present. A successful response without PowerState means On, since older models only answer when on. A timeout or connection refusal means Unreachable.
  - **PowerOff:** do nothing if the state is already Standby or Unreachable; if it's On or Unknown, send KEY_POWER. Re-check after 3s and report.
  - **PowerOn:** send the WoL magic packet (6×0xFF + 16×MAC) as UDP broadcast to 255.255.255.255 and the subnet broadcast, ports 9 and 7, three times 200ms apart. If the TV is reachable in Standby after 4s, also send KEY_POWER. This needs "Power On with Mobile"/network standby enabled on the TV; document it.
- Transport abstraction for tests: the driver depends on `ITvTransport` (WebSocket connect/send/receive, HTTP GET, UDP send) with a real `SamsungTvTransport`. Ports and scheme come from options (`Tv:RemotePort` 8002, `Tv:RestPort` 8001, `Tv:UseTls` true) so tests can point at a local fake.
- **Tests:**
  - Unit tests with a fake transport: pairing token parse, unauthorized handling, power-off skipped when in standby, the WoL packet bytes for a known MAC, and state parsing with and without PowerState.
  - Integration test: a minimal fake Samsung TV in-process (Kestrel on a random port with a WebSocket endpoint and a `/api/v2/` JSON endpoint over plain ws/http, `Tv:UseTls=false`), driven through the real transport.

### Server integration
- **`TvMonitorService`** (`BackgroundService`): every 15s (configurable `Tv:PollSeconds`) it polls `GetStateAsync` for every enabled Console station with a TvIpAddress, caches the state in a singleton `TvStateCache` and raises a change event for the dashboard. Polls run in parallel, each with a 2s timeout.
- **`TvExpiryActionHandler`** (`IExpiryActionHandler` for Console stations): PowerOff on GraceElapsed.
- **Session events for Console stations:**
  - Start or resume: PowerOn (WoL).
  - Stop: PowerOff, controlled by setting `Tv:PowerOffOnStop`, default true.
  - Move: PowerOff the old station and PowerOn the new one.
  - Pause: no TV action (default). The console keeps the game state and paused time isn't billed.
- Driver failures never break session operations. Each is caught, logged and shown as a desk alert or toast ("TV for PS5-01 did not respond"), and recorded in the `AgentCommandLog` that Phase 3 added (or an equivalent device command log).
- **Status:** a Console station with a TvIpAddress is Offline when idle and the TV is Unreachable. The card shows a small TV indicator (On / Standby / Unreachable / Unknown). Session status still overrides.
- **Pairing UI:** on `/stations`, Console rows get a "Pair TV" button that calls `PairAsync` and shows "Accept the prompt on the TV…" with a spinner and the result. On success it saves the token to `Station.TvToken`. A "Test" button runs GetState and shows the result.
- **Dashboard card (Console):** TV On / TV Off buttons and the TV state indicator.
- **Settings page:** `Tv:PowerOffOnStop` toggle and a help panel listing the TV settings to enable: Power On with Mobile / IP remote / network standby.

## Claude's Discretion
- Exact UI placement and copy.
- The poll interval default (15s is fine).

## Deferred
- On-screen TV messages (out of scope for v1).
- Other TV brands (only `ITvDriver` is extensible).

## Environment Notes
- Same as earlier phases: `export PATH="/usr/local/share/dotnet:$PATH"`, net10.0, Microsoft packages only (System.Net.WebSockets.ClientWebSocket and System.Net.Sockets are built in), commits on main as dylenq with no attribution, and leave the untracked runtime files alone.
- No real TV is available. Real-hardware checks go in the SUMMARY as human checks.
