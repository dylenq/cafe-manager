---
phase: 03-pc-agent
plan: 03
subsystem: agent
tags: [agent-core, state-machine, countdown, reconnect, pbkdf2, simulator]
requires:
  - phase: 03-pc-agent
    provides: AgentController tracer, agent contracts, AgentHub (plan 03-01)
provides:
  - Complete platform-neutral AgentController implementing IAgentClient
  - AgentRetryPolicy (0, 2, 5, 10, 30 s forever), EmergencyCodeHasher (PBKDF2), AgentSettingsLoader
  - Cafe.AgentSim console project and in-process SimFleet
affects: [03-05, 04-tv]
status: complete
commits: 3
plan_head_before: fea268ec1daac6b021152b7c2253de78b7346f0c
plan_head_after: 52d4ae61eba2d4f0c023ef103686cefe824b675a
actuals:
  tokens: 30000
  tasks: 3
  commits: 3
tech-stack:
  added: [Microsoft.Extensions.Logging.Console 10.0.15 (AgentSim only)]
  patterns:
    - "Controller owns one lock; display callbacks wrapped so a failing display never breaks it"
    - "All time from IAgentClock; skew = ServerUtcNow - agent now at receipt"
key-files:
  created:
    - src/Cafe.Agent.Core/AgentRetryPolicy.cs
    - src/Cafe.Agent.Core/EmergencyCodeHasher.cs
    - src/Cafe.Agent.Core/AgentSettings.cs
    - src/Cafe.Agent.Core/AgentSettingsLoader.cs
    - src/Cafe.AgentSim/ (Cafe.AgentSim.csproj, Program.cs, SimOptions.cs, SimFleet.cs, SimAgentDevices.cs)
    - tests/Cafe.Tests/AgentControllerStateTests.cs
    - tests/Cafe.Tests/AgentConnectionResilienceTests.cs
    - tests/Cafe.Tests/AgentCoreSecurityTests.cs
    - tests/Cafe.Tests/AgentSimTests.cs
    - tests/Cafe.Tests/Infrastructure/AgentUnitFakes.cs
  modified:
    - src/Cafe.Agent.Core/AgentController.cs
    - Cafe.sln
    - tests/Cafe.Tests/Cafe.Tests.csproj
key-decisions:
  - "Server wins whenever connected: the agent self-locks at zero (or end of grace) only while disconnected"
  - "SessionUpdated while Locked unlocks (server is authoritative), and clears emergency unlock"
  - "Simulator countdown shows total minutes (60:00); open-ended elapsed shows h:mm:ss"
---

# Phase 3 Plan 03: Complete agent core and AgentSim Summary

Platform-neutral agent with skew-corrected local countdown, warnings, grace, forever-retry reconnect, PBKDF2 emergency-code hasher, settings loader, and a simulator running N fake PCs.

## Accomplishments

- AgentController implements all eight IAgentClient methods, registered one-to-one on the hub; locked before first connect and while offline; ticks once a second via IAgentClock.
- Reconnect: backoff 0, 2, 5, 10, 30 s then 30 s forever for both initial connect and SignalR automatic reconnect; bad key or unreachable server never crashes; server re-push replaces local state, including a Lock for a session stopped during the gap.
- EmergencyCodeHasher: `pbkdf2-sha256$200000$salt$hash`, random 16-byte salt, FixedTimeEquals, stored iterations bounded to 100000..5000000, empty hash disables.
- AgentSettingsLoader returns errors (never the key) instead of throwing.
- `dotnet run --project src/Cafe.AgentSim -- --server URL --key KEY --count N --prefix SIM-` prints lines like `[SIM-02] UNLOCKED 60:00 remaining`.
- Full suite: 373 tests pass; `dotnet build Cafe.sln` clean.

## Task Commits

1. Task 1 state machine: 3133cac
2. Task 2 resilience, hasher, loader: e316da8
3. Task 3 AgentSim: 52d4ae6

## Deviations from Plan

- [Rule 3 - Blocking] AgentRetryPolicy was committed with task 1 (the controller depends on it) rather than task 2.
- Added `AgentController.GraceRemainingSeconds` (public) so the simulator can print `GRACE 120s`.
- The simulator formats countdowns as total minutes (`60:00`) to match the plan's example, not the server's `1:00:00`.
- Test fakes live in a new `Infrastructure/AgentUnitFakes.cs` instead of editing the shared harness (avoids a conflict with plan 03-02).
- `SimFleet.RunAsync` takes an optional trailing `ILoggerFactory` so tests can silence console logging.

No auth gates. No stubs. No new network surface beyond the simulator, which uses the existing agent hub and key.

## Human checks pending

- Demo: run Cafe.Server, copy the key from /settings, run the simulator with `--count 3`, create stations SIM-01..03, start and stop a session on SIM-02 and watch the colored lines.
- Confirm on nuget.org that Microsoft.Extensions.Logging.Console 10.0.15 is owned by Microsoft (T-03-SC).

## Self-Check: PASSED

Commits 3133cac, e316da8, 52d4ae6 exist; all listed files present; build and tests green.
