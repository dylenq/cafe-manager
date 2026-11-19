# Cafe Manager

Local management software for a small gaming cafe: station types and stations, timed sessions, PC lock and TV power control. Runs on one server in the cafe and keeps working without internet.

## Run

```bash
export PATH="/usr/local/share/dotnet:$PATH"   # macOS: put the dotnet CLI on PATH
dotnet run --project src/Cafe.Server
```

The server listens on `http://0.0.0.0:5080`, so tablets and phones on the LAN can reach it at `http://<server-ip>:5080`.

## First run

On an empty database every page redirects to `/setup`, where the owner chooses the shared admin password (minimum 8 characters, entered twice). Only a hash is stored. Setup cannot be re-run afterwards to reset the password. Until setup is finished the `/setup` page is only served to requests from the server PC itself (loopback), so do first-run setup on the front-desk PC at `http://localhost:5080`. Devices elsewhere on the LAN see a page explaining that. To allow remote setup (for example a headless server), set `Setup:AllowRemote` to `true` for the first run, and anyone on the LAN can then claim the admin password until you finish, so switch it back afterwards.

## Configuration

| Key | Default | Environment variable |
|-----|---------|----------------------|
| `Kestrel:Endpoints:Http:Url` | `http://0.0.0.0:5080` | `Kestrel__Endpoints__Http__Url` |
| `ConnectionStrings:Cafe` | `Data Source=cafe.db` | `ConnectionStrings__Cafe` |
| `Setup:AllowRemote` | `false` | `Setup__AllowRemote` |
| `Cafe:Culture` | empty (server culture) | `Cafe__Culture` |

`Cafe:Culture` is a culture name such as `en-US` or `vi-VN`. It decides how amounts are formatted (currency symbol, separators) on every screen; leave it empty to use the server's own culture, and an unknown name logs a warning and falls back to that.

`ASPNETCORE_URLS` and the `--urls` argument also override the listen address. The schema is created and updated automatically at startup through EF Core migrations.

## Running the PC agent

The PC agent (`src/Cafe.Agent`, a WPF app) runs on each gaming PC. It keeps the PC locked until the server starts a session, shows countdown warnings, and executes shutdown and restart from the dashboard. It can only be run and tested on Windows; on macOS the project only compiles, and the behavior is exercised with the simulator (see below).

**1. Get the agent key.** On the server open `/settings`, PC agents card, and use the copy button next to the shared key.

**2. Build and copy.** On a dev machine:

```bash
dotnet publish src/Cafe.Agent -c Release -r win-x64 --self-contained false
```

Copy the publish output folder to `C:\CafeAgent` on each PC. The PCs need the .NET 10 Desktop Runtime.

**3. Configure `agentsettings.json`** (next to `Cafe.Agent.exe`):

```json
{
  "ServerUrl": "http://192.168.1.10:5080",
  "SharedKey": "paste-the-key-from-settings",
  "MachineId": "",
  "EmergencyUnlockCodeHash": ""
}
```

- `ServerUrl`: the server address on the LAN.
- `SharedKey`: the key from step 1.
- `MachineId`: leave empty to use the Windows computer name. It must match the station's machine id on the dashboard (case-insensitive).
- `EmergencyUnlockCodeHash`: leave empty to disable emergency unlock, or generate it as below.

**4. Hash the emergency unlock code.** Run `Cafe.Agent.exe --hash-code <code>` from a terminal; it prints the hash and exits without opening any window. Paste it into `EmergencyUnlockCodeHash`.

**5. Register the station.** Create the station on the dashboard with a machine id equal to the PC's, or pick the PC from the Unregistered agents list on `/settings`.

**6. Start the agent.** Run `Cafe.Agent.exe` manually, or put a shortcut to it in the Startup folder (`shell:startup`) so it starts at logon. The scheduled task, Task Manager policy and installer are Phase 7 (DEPL-02) and are not part of this.

### Behavior

- Locked until a session starts, and locked when the server is unreachable and there is no session.
- Warnings appear as a small toast at the bottom right at the configured minutes before the end, without taking focus from a game. During the grace period the toast turns red and says "Time is up — save your work", then the PC locks.
- If the server drops mid-session the countdown keeps running locally and the PC locks at zero. The agent reconnects automatically, at most every 30 seconds.
- While locked, one lock window covers every monitor, and Win, Alt+Tab, Alt+Esc, Ctrl+Esc, Alt+F4 and Ctrl+Shift+Esc are blocked. The hook is active only while locked.
- Ctrl+Alt+Del cannot be intercepted by an application and still works until the Phase 7 installer applies the Task Manager policy.
- The agent never blocks Windows shutdown, restart or logoff.

### Emergency unlock

Press `Ctrl+Alt+Shift+F12` on the lock screen and enter the code. Three wrong codes lock the prompt for 30 seconds. A correct code unlocks the PC until the next server message, and the "Also exit the agent" option stops the agent. This is the only way to exit the agent. If no hash is configured the prompt says unlock is not configured.

### Troubleshooting

- The lock screen shows "Agent configuration error": `agentsettings.json` is missing, not valid JSON, or has no valid `ServerUrl` or `SharedKey`. The PC stays locked until it is fixed and the agent restarted.
- The station shows the agent offline: check `ServerUrl`, the firewall on the server (port 5080) and that the machine id matches the station.
- A 401 in `logs/agent-*.log` (next to the exe, kept 14 days) means the `SharedKey` is wrong or was regenerated on the server.

### Simulator

Without Windows PCs you can drive the server with simulated agents:

```bash
dotnet run --project src/Cafe.AgentSim -- --server http://localhost:5080 --key KEY --count 10 --prefix SIM-
```

Each simulated agent logs its state changes, for example `[SIM-03] UNLOCKED 60:00 remaining`. The real Windows agent can only be tested on Windows hardware; macOS verification uses the simulator.

## Security note

v1 serves plain HTTP on the LAN with no TLS. The admin password and the auth cookie travel in clear text on the cafe network, so keep the cafe network private. The PC agent key also travels in clear text on the LAN, and `agentsettings.json` should be readable only by administrators on the PC. The login is throttled in memory: after 5 failed attempts from one address within 5 minutes, that address is refused for 5 minutes (a server restart clears the counters).

## Tests

```bash
dotnet test Cafe.sln
```
