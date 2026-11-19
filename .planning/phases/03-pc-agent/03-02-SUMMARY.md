---
phase: 03-pc-agent
plan: 02
subsystem: agent
tags: [signalr, session-signals, expiry, dashboard, settings, blazor]
requires:
  - phase: 03-pc-agent
    provides: AgentHub, AgentRegistry, AgentCommandService, SessionSignalPublisher (plan 01)
provides:
  - Full session-to-command mapping (start/resume, pause, extend, move, stop, warning, grace)
  - PcLockExpiryActionHandler (locks PCs after grace, idempotent)
  - On-connect state push covering warning window and grace
  - Registry-backed PC Offline/Idle status and StationCardModel.AgentOffline
  - Dashboard refresh on AgentRegistry.Changed
  - PC agents panel on /settings (key, connected, unregistered, cafe name)
affects: [03-03, 03-04, 03-05, 04-tv]
status: complete
commits: 3
plan_head_before: fea268ec1daac6b021152b7c2253de78b7346f0c
plan_head_after: 47ac34e9c2d632795016582922c717856202dc8b
actuals:
  tokens: 36000
  tasks: 3
  commits: 3
tech-stack:
  added: []
  patterns:
    - "Signals collected in the mutation context (SessionService) and in ExpiryEvaluator.ProcessAsync, published after commit"
    - "Agent commands derived from committed DB state plus TimeProvider, never from the signal payload alone"
key-files:
  created:
    - src/Cafe.Server/Agents/PcLockExpiryActionHandler.cs
    - src/Cafe.Server/Components/Shared/AgentsPanel.razor
    - tests/Cafe.Tests/AgentSessionMappingTests.cs
    - tests/Cafe.Tests/AgentStatusTests.cs
    - tests/Cafe.Tests/AgentsPanelTests.cs
  modified:
    - src/Cafe.Server/Agents/AgentCommandService.cs
    - src/Cafe.Server/Agents/AgentAdminService.cs
    - src/Cafe.Server/Agents/AgentSettingsService.cs
    - src/Cafe.Server/Agents/AgentServiceCollectionExtensions.cs
    - src/Cafe.Server/Sessions/SessionService.cs
    - src/Cafe.Server/Sessions/ExpiryEvaluator.cs
    - src/Cafe.Server/Dashboard/DefaultStationStatusProvider.cs
    - src/Cafe.Server/Dashboard/DashboardService.cs
    - src/Cafe.Server/Dashboard/StationCardModel.cs
    - src/Cafe.Server/Components/Pages/Home.razor
    - src/Cafe.Server/Components/Pages/Settings.razor
key-decisions:
  - "ExpiryEvaluator gets a trailing optional SessionSignalPublisher parameter; existing direct constructions keep compiling"
  - "Warnings: only the smallest newly fired threshold is signalled and only if the session is still Active; GraceStarted only when Grace was entered in the pass, the session did not end, and grace seconds remain"
  - "PcLockExpiryActionHandler does not throw when the agent is offline; the on-connect push locks it on reconnect"
  - "AgentAdminService (singleton) resolves the scoped StationService per call through IServiceScopeFactory"
  - "Assigning an agent to a station that already had another machine id also re-pushes state to the replaced agent so it locks as unregistered"
requirements-completed: []
---

# Phase 3 Plan 02: Session Mapping, Status and Agents Panel Summary

Every session lifecycle and expiry event now reaches the right PC as the right command, the dashboard model reports real online/offline from the agent registry, and staff manage the shared key, unregistered PCs and the cafe name from /settings.

## Tasks

| Task | Name | Commit |
| ---- | ---- | ------ |
| 1 | Full session-to-command mapping, state push on connect, PC lock expiry handler | 322ac61 |
| 2 | Registry-backed Offline status, card flag and instant dashboard refresh | 2e6d97b |
| 3 | PC agents section on /settings | 47ac34e |

## Verification

- `dotnet build Cafe.sln`: 0 warnings, 0 errors.
- `dotnet test Cafe.sln`: 358 passed (333 after plan 01 plus 25 new: 14 AgentSessionMappingTests, 5 AgentStatusTests, 6 AgentsPanelTests).
- The 02-REVIEW-FIX changes in ExpiryEvaluator and SessionService (durable retried handlers, handler timeout, grace-elapsed checks) are intact; all existing ExpiryEvaluator, SessionTicker and SessionExtendMove tests pass.
- Acceptance greps: IExpiryActionHandler registered in AddAgentServices and LoggingExpiryActionHandler still in Program.cs; all four Paused/Resumed/Extended/Moved signals in SessionService; no DateTime.Now/UtcNow in Agents or Sessions; AgentOffline is in StationCardModel and DashboardService but not in StationCard.razor; AgentsPanel has Registry.Changed subscribe and unsubscribe and no MarkupString.

## Requirements

AGNT-01, AGNT-03 and AGNT-04 are not marked complete: the server half is done, but the WPF/agent-side handling (SessionUpdated, ShowWarning, GraceStarted in AgentController, plan 03-03) and the StationCard offline indicator (plan 03-04) are still outstanding.

## Deviations from Plan

None - plan executed as written. Minor additions beyond the letter of the plan:
- [Rule 2] AssignAgentToStationAsync also pushes state to the previously assigned machine id so a replaced agent does not keep a stale unlock.
- AgentAdminService gained `ListPcStationsAsync` (read model for the assign dropdown) and takes `IServiceScopeFactory` and `AgentCommandService` as constructor dependencies.

## Known Stubs

None.

## Threat Flags

None beyond the plan's threat model. T-03-06 (key only on the authenticated /settings page, masked by default, anonymous redirect tested, never logged), T-03-07 (create/assign only inside the authenticated panel; unregistered agents are told to Lock), T-03-08 (PC with a machine id is Offline without a live authenticated connection) and T-03-09 (smallest-warning-only, GraceStarted suppressed after end) are implemented and covered by tests.

## Human checks pending

- On the cafe server open /settings: the PC agents card shows the key; Show, Copy and Regenerate (with the inline confirmation) work in a real browser; a connected unknown agent (AgentSim from plan 03-03 with an unused prefix) appears under Unregistered agents and Create station and Assign both work. The Blazor button interactivity has no browser harness in the test suite.
- Dashboard: confirm a PC card flips between Offline and Idle within a moment of the agent connecting or disconnecting (Home refresh on AgentRegistry.Changed is not covered by a browser test).

## Self-Check: PASSED

Files verified present: PcLockExpiryActionHandler.cs, AgentsPanel.razor, AgentSessionMappingTests.cs, AgentStatusTests.cs, AgentsPanelTests.cs. Commits 322ac61, 2e6d97b, 47ac34e verified in git log.
