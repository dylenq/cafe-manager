---
phase: 03-pc-agent
plan: 01
subsystem: agent
tags: [signalr, hub, shared-key-auth, agent-controller, session-signals]
requires:
  - phase: 02-session-engine-pricing
    provides: SessionService mutation skeleton, SessionClock, SessionNotifier
provides:
  - Cafe.Shared agent contracts (IAgentClient, LockInfo, SessionInfo, AgentProtocol)
  - Cafe.Agent.Core AgentController (connect loop, Lock/Unlock) and platform-neutral abstractions
  - AgentHub at /hubs/agent with shared-key middleware, AgentRegistry, AgentCommandService
  - Multicast ISessionEventSink seam with fault-isolated SessionSignalPublisher
  - AgentAdminService read models (connected, unregistered) and key regeneration
affects: [03-02, 03-03, 03-04, 03-05, 04-tv]
status: complete
commits: 3
plan_head_before: 9e537c92d5284bdcd23d7239cfafdbd98ac0e4a1
plan_head_after: 01b68ed53cbf326c18124c851bb8d73793e9c009
actuals:
  tokens: 21000
  tasks: 2
  commits: 3
tech-stack:
  added: [Microsoft.AspNetCore.SignalR.Client 10.0.15, Microsoft.Extensions.Logging.Abstractions 10.0.15]
  patterns:
    - "Typed Hub<IAgentClient> with state push on every connect"
    - "Signals collected in the mutation context and published after commit; sinks isolated by try/catch"
key-files:
  created:
    - src/Cafe.Shared/Agents/IAgentClient.cs
    - src/Cafe.Shared/Agents/AgentDtos.cs
    - src/Cafe.Shared/Agents/AgentProtocol.cs
    - src/Cafe.Agent.Core/AgentController.cs
    - src/Cafe.Server/Agents/AgentHub.cs
    - src/Cafe.Server/Agents/AgentRegistry.cs
    - src/Cafe.Server/Agents/AgentAuthMiddleware.cs
    - src/Cafe.Server/Agents/AgentSettingsService.cs
    - src/Cafe.Server/Agents/AgentCommandService.cs
    - src/Cafe.Server/Agents/AgentAdminService.cs
    - src/Cafe.Server/Sessions/ISessionEventSink.cs
    - src/Cafe.Server/Sessions/SessionSignalPublisher.cs
    - tests/Cafe.Tests/AgentTracerTests.cs
    - tests/Cafe.Tests/AgentHubAuthTests.cs
  modified:
    - src/Cafe.Server/Sessions/SessionService.cs
    - src/Cafe.Server/Auth/SetupGateMiddleware.cs
    - src/Cafe.Server/Program.cs
    - Cafe.sln
key-decisions:
  - "Hub route exempt from the setup gate by prefix /hubs; the key middleware is its only gate"
  - "PushStateAsync derives Unlock/Lock from the database, so Started and Stopped signals both just re-push state"
  - "AgentController reconnects by restarting its own connect loop on Closed, using the 0,2,5,10,30s schedule"
requirements-completed: []
---

# Phase 3 Plan 01: Agent Tracer and Connection Edge Summary

A real AgentController connects to the AgentHub through the TestServer, is locked on connect, unlocked with a 3600 s SessionInfo when a prepaid session starts and locked again on stop; the hub is gated by a constant-time shared key and a validated machine id.

## Tasks

| Task | Name | Commit |
| ---- | ---- | ------ |
| 1 | Tracer: connect, lock, unlock on start, lock on stop | 2aa364f |
| 2 | Harden the connection edge (regeneration, machine id validation, admin read models) | 6556664, 01b68ed (log wording) |

## Verification

- `dotnet build Cafe.sln`: 0 warnings, 0 errors.
- `dotnet test Cafe.sln`: 333 passed (13 new: 5 tracer, 8 hub auth).
- The flaky `SessionTickerTests` test did not fail.

## Requirements

AGNT-01/02/03/06 are not marked complete: this plan delivers only the server/Core half (no dashboard online badge, no WPF lock screen, no disconnected-lock behavior). They stay pending until the later plans land.

## Deviations from Plan

- [Rule 2 - Missing critical] The auth middleware's rejection log line was reworded so no log line mentions "key", satisfying the T-03-02 acceptance grep (commit 01b68ed). The key and machine id are never logged.
- Test count: the plan listed 5 tracer behaviors; one tracer test was split into two (lock/unlock/lock via the real controller, and Unlock payload via the raw recorder) because two connections with one machine id replace each other by design. An extra test covers connect-while-session-active.
- AgentRegistry, the full `RegenerateSharedKeyAsync` wiring and `DisconnectAll` were written in Task 1 alongside the hub (the hub needs them); Task 2 added regeneration, machine id validation and AgentAdminService.

## Known Stubs

None. `AgentController` does not yet handle SessionUpdated/ShowWarning/GraceStarted/ShowMessage/Shutdown/Restart, and AgentCommandService only reacts to Started/Stopped signals; both are intentional and owned by plans 03-02 and 03-03. `IPowerControl`/`IAgentClock` are held but unused until 03-03.

## Threat Flags

None beyond the plan's threat model. T-03-01/02/04/05 are mitigated as specified; the NuGet packages are Microsoft first-party (owner check on nuget.org is a human check, below).

## Human checks pending

- Confirm on nuget.org that Microsoft.AspNetCore.SignalR.Client and Microsoft.Extensions.Logging.Abstractions 10.0.15 are owned by "Microsoft" (T-03-SC).
- Optional: `dotnet run --project src/Cafe.Server` and confirm startup logs no SignalR mapping errors.

## Self-Check: PASSED

Files and commits (2aa364f, 6556664, 01b68ed) verified present.
