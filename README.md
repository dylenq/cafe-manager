# Cafe Manager

Local management software for a small gaming cafe (under 15 stations): Windows gaming PCs plus consoles on Samsung Tizen smart TVs. Front-desk staff use one browser-based admin app to start and bill timed sessions, lock and unlock PCs, power off console TVs when time runs out, manage members and prepaid time, sell food and drinks, print receipts and see revenue. It runs on one server in the cafe and keeps working without internet.

Features:

- Sessions and pricing: stations and station types, prepaid, open-ended and balance-funded sessions, pause, extend, move, grace period, time-of-day rate rules, member rates.
- PC lock agent: a Windows agent keeps each PC locked until a session starts, shows countdown warnings, and supports remote lock, unlock, restart, shutdown and messages, plus an emergency unlock.
- Samsung TV control: pairing, Wake-on-LAN power on, power off when a session ends, live TV status.
- Customers and packages: customers and members, prepaid time packages, balance buckets, ledger.
- POS and receipts: products, walk-in orders, session tabs, split cash and card payments, voids, receipts (browser, network ESC/POS or Windows printer).
- Reports: revenue by day, week, station type, category, product and payment method, with CSV export.
- Backups: nightly database backups with retention, and a "Back up now" button.

## Install (for the cafe)

Use the release bundle `CafeManager-1.1.0-win-x64.zip` (see "Build the release bundle" below to produce it).

- [docs/INSTALL.md](docs/INSTALL.md): server, first-run setup, agents on gaming PCs, TVs, printer, backups, upgrades.
- [docs/USER-GUIDE.md](docs/USER-GUIDE.md): the daily staff guide.

The bundle contains `install-server.ps1`, `install-agent.ps1` and matching uninstall scripts (sources in [deploy/windows](deploy/windows)).

## Development quick start

```bash
export PATH="/usr/local/share/dotnet:$PATH"   # macOS: put the dotnet CLI on PATH
dotnet run --project src/Cafe.Server
```

The server listens on `http://0.0.0.0:5080`, so tablets and phones on the LAN can reach it at `http://<server-ip>:5080`. On an empty database every page redirects to `/setup`, where the owner chooses the shared admin password (minimum 8 characters, entered twice; only a hash is stored, and setup cannot be re-run to reset it). Until setup is finished `/setup` is only served to requests from the server PC itself (loopback), so do first-run setup at `http://localhost:5080`. For a headless server set `Setup:AllowRemote` to `true` for the first run only: anyone on the LAN can claim the password until you finish.

Run the tests:

```bash
dotnet test Cafe.sln
```

### Try it without Windows PCs (AgentSim)

The WPF agent only runs on Windows (on macOS it compiles only). `Cafe.AgentSim` simulates agents:

1. In the app, open Settings, PC agents card, and copy the shared key.
2. Create a PC station with "Agent machine ID" `SIM-01` (or pick `SIM-01` from "Unregistered agents").
3. Run:

```bash
dotnet run --project src/Cafe.AgentSim -- --server http://localhost:5080 --key <shared-key> --count 3 --prefix SIM-
```

Each simulated agent logs its state changes, for example `[SIM-03] UNLOCKED 60:00 remaining`. Options: `--server`, `--key` (or the `CAFE_AGENT_KEY` environment variable), `--count` (1 to 200), `--prefix`, `--no-color`.

## Build the release bundle

```bash
build/build-release.sh          # macOS or Linux
build\build-release.ps1         # Windows (PowerShell)
```

It runs the test suite, publishes the server and agent for win-x64 (self-contained), adds the scripts and docs, and writes `dist/CafeManager-<version>-win-x64.zip` (the version comes from `Directory.Build.props`). Set `SKIP_TESTS=1` to skip tests for a trial build only. `dist/` is not tracked by git. The zip is unsigned in v1.

## Project layout

```
src/
  Cafe.Server/      ASP.NET Core + Blazor admin app, EF Core + SQLite, SignalR hub
  Cafe.Shared/      contracts shared by the server and the agents
  Cafe.Agent.Core/  agent logic that is testable without Windows
  Cafe.Agent/       the Windows (WPF) PC agent
  Cafe.AgentSim/    console simulator of PC agents
tests/Cafe.Tests/   automated tests
deploy/windows/     install and uninstall PowerShell scripts
build/              release bundle build scripts
docs/               INSTALL.md and USER-GUIDE.md
.planning/          project planning history (GSD)
```

## Configuration

| Key | Default | Environment variable |
|-----|---------|----------------------|
| `Kestrel:Endpoints:Http:Url` | `http://0.0.0.0:5080` | `Kestrel__Endpoints__Http__Url` |
| `ConnectionStrings:Cafe` | empty (see below) | `ConnectionStrings__Cafe` |
| `Cafe:DataDirectory` | `%ProgramData%\CafeManager` | `Cafe__DataDirectory` |
| `Setup:AllowRemote` | `false` | `Setup__AllowRemote` |
| `Cafe:Culture` | empty (server culture) | `Cafe__Culture` |
| `Cafe:TimeZone` | empty (server time zone) | `Cafe__TimeZone` |
| `Backup:Hour` | `4` | `Backup__Hour` |
| `Backup:Keep` | `14` | `Backup__Keep` |
| `Backup:Directory` | empty (`backups` under the data folder) | `Backup__Directory` |
| `Backup:RunScheduler` | `true` | `Backup__RunScheduler` |

In Production, or when running as a Windows service, the database defaults to `%ProgramData%\CafeManager\cafe.db` (with `logs` and `backups` beside it). A development run uses `cafe.db` in the project folder. An explicit `ConnectionStrings:Cafe` always wins. The schema is created and updated at startup through EF Core migrations.

`Cafe:Culture` is a culture name such as `en-US` or `vi-VN` and decides how amounts are formatted; `Cafe:TimeZone` (for example `Asia/Ho_Chi_Minh`) decides the cafe day for rate rules and reports. An unknown value logs a warning and falls back to the server's own. `ASPNETCORE_URLS` and `--urls` also override the listen address. The full reference, including the background-job switches, is in [docs/INSTALL.md](docs/INSTALL.md).

## The PC agent in brief

The agent (`src/Cafe.Agent`, WPF) runs on each gaming PC and is installed with `install-agent.ps1` (see [docs/INSTALL.md](docs/INSTALL.md)). It stays locked until the server starts a session, shows warnings without taking focus from a game, locks at zero (also when the server is unreachable), and never blocks Windows shutdown or logoff. The emergency unlock is `Ctrl+Alt+Shift+F12` on the lock screen with the code chosen at install (minimum 6 characters; `Cafe.Agent.exe --hash-code <code>` prints the stored hash). Three wrong codes lock the prompt for 30 seconds, doubling on repeats up to 15 minutes.

## Security note

v1 serves plain HTTP on the LAN with no TLS. The admin password, the auth cookie and the agent shared key travel in clear text on the cafe network, so keep that network private. Login is throttled in memory: after 5 failed attempts from one address within 5 minutes, that address is refused for 5 minutes (a server restart clears the counters). Release binaries are not code-signed in v1, so Windows SmartScreen shows a warning.
