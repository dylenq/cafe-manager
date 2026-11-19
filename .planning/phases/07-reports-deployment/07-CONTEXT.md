# Phase 7: Reports & Deployment — Context

**Source:** The approved project plan and the requirements Q&A, written 2026-11-20. The user asked for autonomous execution and wants a finished end product, so Claude made the decisions marked (default).

## Phase Boundary

- Staff can view revenue reports.
- The server installs and runs as a Windows service on the front-desk PC.
- An installer script sets up the PC agent on gaming PCs.
- A release bundle (zip) with installers and documentation is produced. It is the end product handed to the user.

## Existing Code to Build On
- `Payment` + `PaymentAllocation` (Phase 6) with targets Session, Order and PackageSale. `Session`/`SessionSegment` with station types, `OrderLine` with product snapshots, and the cafe time zone setting `Cafe:TimeZone`.
- The server already calls `UseWindowsService()` and listens on `0.0.0.0:5080` (Phase 1).
- `src/Cafe.Agent` WPF (Phase 3): `agentsettings.json` with ServerUrl, SharedKey, MachineId and EmergencyUnlockCodeHash, plus the `--hash-code` switch.
- Read the earlier phases' SUMMARY.md files for exact names.

## Locked Decisions

### Reports (REPT-01, REPT-02): `/reports`
- **Basis (default):** cash basis. Revenue = payment allocations, dated by Payment.AtUtc converted to cafe local time. Also show a "Charged (unpaid)" figure = outstanding amounts in the range, so owed money is visible.
- **Range picker:** Today, Yesterday, This week (Mon–Sun), Last 7 days, This month, Last month and Custom (from/to dates). Default is This week.
- **Sections:**
  - **Summary tiles:** total collected, cash, card, number of sessions, total session hours and average session length.
  - **Revenue by day:** a table plus a simple bar chart drawn as **inline SVG** (no JS chart library or CDN). It works in light and dark mode using CSS variables.
  - **Revenue by week:** ISO weeks starting Monday, as a table.
  - **By station type:** session allocations grouped by the station type of the session's first segment, with hours played.
  - **By product:** quantity sold and revenue from order allocations. Partial payments split proportionally across lines; document this in a code comment.
  - **By category,** and **by payment method.**
- Each table has an "Export CSV" button: a minimal API endpoint `/reports/export/{section}?from=&to=` that requires auth and streams CSV.
- `Reports/ReportService` holds the pure aggregation logic and is heavily tested. Test the time zone day boundary (a payment at 23:30 local belongs to that local day), weeks starting Monday, proportional split and empty ranges.

### Server deployment (DEPL-01)
- **Production data location:** when `ConnectionStrings:Cafe` is not set and the app runs as a Windows service or in Production, default to `%ProgramData%\CafeManager\cafe.db`, creating the folder as needed. Development keeps `cafe.db` in the content root. Logs go to `%ProgramData%\CafeManager\logs` with a simple file logger: write a minimal `ILoggerProvider` rather than adding a third-party package, using daily files and keeping 14 days.
- **Nightly backup:** a `BackupService` (`BackgroundService`) copies the SQLite DB with `SqliteConnection.BackupDatabase` to `%ProgramData%\CafeManager\backups\cafe-YYYYMMDD-HHmm.db` every day at `Backup:Hour` (default 4 local time) and keeps the newest `Backup:Keep` (default 14). Settings also get a "Back up now" button and show the last backup time.
- Scripts in `deploy/windows/`, PowerShell 5.1-compatible:
  - **`install-server.ps1`** (requires admin; self-checks and re-launches elevated):
    - Copy the published server to `C:\Program Files\CafeManager\Server`.
    - Create or update the service with `New-Service` / `sc.exe`: name `CafeManager`, display name "Cafe Manager Server", automatic (delayed) start, recovery set to restart on failure via `sc.exe failure`.
    - Add the firewall rule "Cafe Manager (TCP 5080)" for Private/Domain profiles, start the service and print the LAN URL(s), e.g. `http://192.168.x.x:5080`.
    - It's idempotent, so it can be re-run for upgrades: stop, copy, start.
  - **`uninstall-server.ps1`:** stop and delete the service and firewall rule. It keeps ProgramData unless `-RemoveData` is passed.
- First-run setup must be done on the server PC itself (Phase 1 WR-08 loopback rule), so the installer opens `http://localhost:5080` at the end.

### Agent deployment (DEPL-02)
- **`deploy/windows/install-agent.ps1`** takes `-ServerUrl` (required), `-SharedKey` (required), `-EmergencyCode` (required), `-MachineId` (optional) and `-GamingUser` (optional, defaults to the current user). It requires admin and self-elevates.
  - Copy the published agent to `C:\Program Files\CafeManager\Agent`.
  - Write `agentsettings.json`, getting the code hash by running `Cafe.Agent.exe --hash-code <code>`.
  - Register the scheduled task "CafeManager Agent": trigger at logon of `-GamingUser`, run with highest privileges, restart every 1 minute up to 999 times on failure, no time limit, and not stopping on battery.
  - Set `DisableTaskMgr=1` under that user's `HKU\<SID>\Software\Microsoft\Windows\CurrentVersion\Policies\System`. If the user isn't logged in, `reg load` their NTUSER.DAT into a temp hive and unload it afterwards.
  - Optionally add the firewall rule (outbound is usually allowed; skip it).
  - Start the agent now if the gaming user is logged in.
- **`uninstall-agent.ps1`:** remove the task, the policy value and the files.
- Validate every script with `pwsh -NoProfile -Command "[System.Management.Automation.Language.Parser]::ParseFile(...)"` when pwsh is available. Otherwise review them carefully and note in the SUMMARY that a Windows run is a human check.

### Release bundle (the end product)
- **`build/build-release.sh`** (macOS/Linux, the one we can run here) plus an equivalent **`build/build-release.ps1`**:
  - `dotnet test Cafe.sln` must pass first.
  - `dotnet publish src/Cafe.Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist/stage/Server`.
  - `dotnet publish src/Cafe.Agent -c Release -r win-x64 --self-contained true -o dist/stage/Agent`. EnableWindowsTargeting makes this work on macOS; if WPF self-contained publish fails off-Windows, record it and fall back to framework-dependent with a note.
  - Copy `deploy/windows/*.ps1`, `docs/*.md` and a top-level `README.txt`.
  - Zip to `dist/CafeManager-<version>-win-x64.zip`. The version comes from `Directory.Build.props` `<Version>1.0.0</Version>`.
  - Also publish `Cafe.AgentSim` for the current OS into `dist/stage/tools/AgentSim-<rid>`, so the user can demo without Windows PCs.
- `dist/` is gitignored, so the zip is not committed.
- **Docs:**
  - `docs/INSTALL.md`: front-desk server install, first-run setup, finding the LAN URL, agent install on each gaming PC (where to find the shared key in Settings), Samsung TV setup (Power On with Mobile, IP remote, pairing), receipt printer setup, backups/restore, upgrading and uninstalling.
  - `docs/USER-GUIDE.md`: daily staff operations — starting/extending/moving/stopping sessions, alerts, customers and packages, POS and tabs, settling up, receipts, reports, and troubleshooting (agent offline, TV unreachable, emergency unlock).
  - Update the root `README.md` with an overview, a dev quick start (`dotnet run`, AgentSim demo), the project layout and links to docs.

### Testing
- ReportService unit tests (above) and page smoke tests for /reports and the CSV endpoints (auth required, correct headers).
- BackupService test: a backup file is created and retention is pruned (temp dir, FakeTimeProvider).
- Default data-path resolution tests (Development vs Production/service, explicit connection string wins).
- Finally, run `build/build-release.sh` successfully and list the zip contents in the SUMMARY.

## Claude's Discretion
- Report page layout and chart styling.
- Doc wording and structure.

## Deferred
- Code signing of the binaries and an MSI installer → v2 (Windows SmartScreen will warn; document "More info → Run anyway").
- HTTPS on LAN → v2.

## Environment Notes
- Same as earlier phases: `export PATH="/usr/local/share/dotnet:$PATH"`, net10.0, Microsoft packages only, EF migrations via `dotnet tool run dotnet-ef` if any, commits on main as dylenq with no attribution, and leave the untracked runtime files alone. Check whether `pwsh` exists before relying on it.
