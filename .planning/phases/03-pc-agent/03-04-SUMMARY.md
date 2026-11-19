---
phase: 03-pc-agent
plan: 04
subsystem: agent
tags: [signalr, ef-migration, audit-log, blazor, integration-tests]
requires:
  - phase: 03-pc-agent
    provides: AgentHub, AgentRegistry, AgentCommandService, AgentController, StationCardModel.AgentOffline (plans 01-03)
provides:
  - AgentCommandLog table and the single phase migration AddAgentCommandLog
  - AgentRemoteControlService (validated, time-boxed, audited Lock/Unlock/Message/Shutdown/Restart)
  - AgentCommandLogService (newest first, station filter, 200 cap)
  - PcCommandMenu on PC station cards, Agent offline badge, offline-start notice
  - Agent commands tab on /sessions (also reachable at /sessions?tab=commands)
  - AgentLifecycleIntegrationTests: real AgentController end-to-end net for AGNT-01..06
affects: [03-05, 04-tv]
status: complete
commits: 3
plan_head_before: 1784cc7bbd40827e2cd9a500cd0f1d0c006c6ca7
plan_head_after: c78e49a5c016721bfa76be81a628d38a876d5527
actuals:
  tokens: 40000
  tasks: 3
  commits: 3
tech-stack:
  added: []
  patterns:
    - "Audit row written for every attempt on a known station (delivered, offline, validation, failed); no FK so it survives station deletion"
    - "Tabs driven by component state with ?tab= for the initial tab; no JavaScript"
key-files:
  created:
    - src/Cafe.Server/Data/AgentCommandLog.cs
    - src/Cafe.Server/Data/Migrations/20261119214900_AddAgentCommandLog.cs
    - src/Cafe.Server/Data/Migrations/20261119214900_AddAgentCommandLog.Designer.cs
    - src/Cafe.Server/Agents/AgentRemoteControlService.cs
    - src/Cafe.Server/Agents/AgentCommandLogService.cs
    - src/Cafe.Server/Components/Shared/PcCommandMenu.razor
    - tests/Cafe.Tests/AgentRemoteControlTests.cs
    - tests/Cafe.Tests/PcCommandMenuTests.cs
    - tests/Cafe.Tests/AgentLifecycleIntegrationTests.cs
  modified:
    - src/Cafe.Server/Data/CafeDbContext.cs
    - src/Cafe.Server/Data/Migrations/CafeDbContextModelSnapshot.cs
    - src/Cafe.Server/Agents/AgentServiceCollectionExtensions.cs
    - src/Cafe.Server/Components/Shared/StationCard.razor
    - src/Cafe.Server/Components/Pages/Sessions.razor
    - src/Cafe.Server/Dashboard/StationCardModel.cs
    - src/Cafe.Server/Dashboard/DashboardService.cs
key-decisions:
  - "Message audit Details is 'Delivered: {text}' (truncated to 300 with an ellipsis); other commands log 'Delivered'"
  - "Delivery failures log 'Failed: {exception message}' and return 'Could not reach the PC agent. Try again.'; no stack traces anywhere"
  - "PC menu is an inline panel in the card (no absolute positioning, no Bootstrap JS), closed by default"
  - "StationCardModel gained a trailing optional HasAgent; the menu shows only for PC stations with a machine id"
  - "Offline-start notice goes through the existing OnNotice, with an inline dismissible alert fallback"
requirements-completed: [AGNT-05]
---

# Phase 3 Plan 04: Remote Control, Audit Trail and Lifecycle Tests Summary

Staff can lock, free-unlock, message, shut down and restart a PC from a menu on its station card, every attempt is audited in AgentCommandLog and shown on /sessions under Agent commands, and an 11-test lifecycle suite drives real AgentController instances against the real server.

## Tasks

| Task | Name | Commit |
| ---- | ---- | ------ |
| 1 | AgentCommandLog entity, single migration, remote-control and log services | 6fb329d |
| 2 | PC command menu, Agent offline indicator and offline-start notice | 7df17b5 |
| 3 | Agent commands tab on /sessions and the full-lifecycle integration test | c78e49a |

## Verification

- `dotnet build Cafe.sln`: 0 warnings, 0 errors.
- `dotnet test Cafe.sln`: 424 passed (358 before plan 03-03 plus its additions, plus 10 AgentRemoteControlTests, 5 PcCommandMenuTests, 11 AgentLifecycleIntegrationTests). Two consecutive full runs green after one flaky-test fix (below).
- `dotnet tool run dotnet-ef migrations has-pending-model-changes`: no changes. The migration Up creates only AgentCommandLogs and its two indexes.
- Acceptance greps: no DateTime.Now/UtcNow in Agents; no MarkupString in PcCommandMenu or Sessions; PcCommandMenu appears once in StationCard; "PC agent offline" in both StationCard and PcCommandMenu.
- The 02-REVIEW-FIX StationCard stop-confirm binding (confirm tied to session id) is untouched; all additions are additive.

## Requirements

AGNT-05 is marked complete (dashboard commands with confirmations and audit). AGNT-01/02/03/04/06 stay pending: the end-to-end proof now exists against the platform-neutral controller, but the Windows WPF lock overlay and real agent come from plan 03-05.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug, test only] Race in the unregistered-machine lifecycle test**
- **Found during:** full-suite run after Task 3
- **Issue:** the test read the agent's last lock reason right after the initial local lock, before the server's push arrived; failed once under load.
- **Fix:** wait for the lock reason to equal AgentCommandService.UnknownMachineReason.
- **Files modified:** tests/Cafe.Tests/AgentLifecycleIntegrationTests.cs (included in commit c78e49a)

### Other notes

- The Agent commands panel is reachable server-rendered at `/sessions?tab=commands` (a `[SupplyParameterFromQuery]` initial-tab parameter); tab clicks switch by component state. This is an addition to make the tab testable and linkable; behavior otherwise as planned.
- The Agent commands render test lives in AgentLifecycleIntegrationTests (not AgentRemoteControlTests); the log-service cases are in AgentRemoteControlTests.
- AgentRemoteControlTests has 10 test cases (the plan listed 7 behaviors; the blank-message check is a 3-case theory).

## Known Stubs

None.

## Threat Flags

None beyond the plan's threat model. T-03-16 (no anonymous endpoint; commands only from authenticated Blazor components, station kind and agent id validated), T-03-17 (confirmations plus in-flight disabling), T-03-18 (audit row per attempt, no FK), T-03-19 (control characters stripped, 500 char cap, Razor encoding verified by a test with angle brackets) are implemented.

## Human checks pending

- With AgentSim (plan 03-03) running and a station for SIM-01: the PC menu appears on the card; Lock, Free unlock, Message, Shutdown and Restart show their confirmations; the sim prints the matching line; stopping the sim disables the menu and shows the Agent offline badge on a running session; starting a session on the offline PC shows the notice "PC agent offline — the PC will unlock when it reconnects."
- Open /sessions in a browser: the Agent commands tab lists commands sent from the dashboard menu with correct local times, and switching tabs keeps the station filter.
- Phone layout check of the menu panel (buttons wrap, usable touch size).

## Self-Check: PASSED

Files verified present; commits 6fb329d, 7df17b5, c78e49a verified in git log.
