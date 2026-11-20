---
phase: 04-samsung-tv-control
plan: 02
subsystem: devices
tags: [session-sink, tv-monitor, state-cache, audit-log, dashboard-status]
requires:
  - phase: 04-samsung-tv-control
    provides: ITvDriver, SamsungTizenDriver, TvExpiryActionHandler (plan 01)
provides:
  - TvControlService (logged, cached, notice-raising TV power actions)
  - TvSessionEventSink (ISessionEventSink for Console stations) with TvActionQueue (latest wins per station)
  - TvMonitorService and TvStateCache (change-only Changed event, desk notice event)
  - TV-aware DefaultStationStatusProvider and StationCardModel.HasTv / TvState
  - FakeTvDriver test double
affects: [04-03, 04-04]
status: complete
commits: 2
plan_head_before: 7992be875c7a265ca607ec206416759f66250c3f
plan_head_after: ade762647b0cdc335dec0dfa7f9e917b44073499
actuals:
  tokens: 28000
  tasks: 2
  commits: 2
tech-stack:
  added: []
  patterns:
    - "Sink only enqueues; TV work runs on a per-station latest-wins queue with a timeout, so a dead TV never delays a session call"
    - "Every TV attempt (manual, session, expiry) goes through TvControlService: audit row, cache update, de-duplicated desk notice"
key-files:
  created:
    - src/Cafe.Server/Devices/TvStateCache.cs
    - src/Cafe.Server/Devices/TvSettingsService.cs
    - src/Cafe.Server/Devices/TvControlService.cs
    - src/Cafe.Server/Devices/TvActionQueue.cs
    - src/Cafe.Server/Devices/TvSessionEventSink.cs
    - src/Cafe.Server/Devices/TvMonitorService.cs
    - tests/Cafe.Tests/Infrastructure/FakeTvDriver.cs
    - tests/Cafe.Tests/TvSessionSinkTests.cs
    - tests/Cafe.Tests/TvMonitorStatusTests.cs
  modified:
    - src/Cafe.Server/Data/AgentCommandLog.cs
    - src/Cafe.Server/Devices/TvOptions.cs
    - src/Cafe.Server/Devices/TvExpiryActionHandler.cs
    - src/Cafe.Server/Devices/TvServiceCollectionExtensions.cs
    - src/Cafe.Server/Dashboard/DefaultStationStatusProvider.cs
    - src/Cafe.Server/Dashboard/DashboardService.cs
    - src/Cafe.Server/Dashboard/StationCardModel.cs
    - tests/Cafe.Tests/TvDriverTests.cs
key-decisions:
  - "AgentCommandKind gains TvPowerOn = 5 and TvPowerOff = 6 (int column, no migration)"
  - "Audit Details read '<source>: <outcome>' where source is Manual, Expiry, Session start, Session stop or Session move; failures contain 'Failed'"
  - "TvControlService abandons a driver that ignores cancellation (WaitAsync), so the queue timeout always produces a Failed audit row and notice"
requirements-completed: [TVCT-03]
---

# Phase 4 Plan 2: Server wiring for TV control Summary

Console session start, resume, stop and move now drive the TV in the background through a latest-wins queue, every TV action is audited and failures surface as de-duplicated desk notices, and a TvMonitorService keeps a state cache that the station status and card models use.

## Tasks

1. TvControlService, queue, sink, settings, expiry delegation (e677b57): 14 tests in TvSessionSinkTests covering start, resume, pause, stop, the PowerOffOnStop setting, move (active and paused), stop-then-start, driver exception and Failed outcome, hanging driver, manual commands and the expiry handler.
2. TvMonitorService, status and card fields (ade7626): 14 tests in TvMonitorStatusTests covering parallel polling with a hanging and a throwing TV, cache removal, change-only events, the provider matrix, stale entries, session override, card fields and RunMonitor.

Build: 0 warnings. Full suite: 560 passed (544 after task 1). No csproj or migration changes, no new packages.

## Deviations from Plan

**1. [Rule 1 - Bug] Queue timeout raced the audit write.** First draft wrapped the action in the queue with its own WaitAsync, so the queue could finish before TvControlService wrote the Failed row. Removed it; TvControlService owns the abandon-on-timeout and the queue relies on the token.

**2. [Rule 3 - Blocking] TvDriverTests.HandlerTests constructed TvExpiryActionHandler with a driver.** Updated the helper to build a TvControlService, because the handler now takes the service as the plan requires.

**3. Audit Details wording.** The plan asked for Details starting "Failed" for failures and "Manual" / "Expiry" for sources; these cannot all be prefixes. Details are '<source>: <outcome>' and failures contain "Failed" (tests assert Contains).

## Known Stubs

None.

## Human checks pending

- Real Samsung TV: start a session on a console whose TV is in standby and confirm it wakes within about 10 s; stop it and confirm it switches off; with Tv:PowerOffOnStop=false (plan 03 toggle) confirm it stays on.
- Models without network standby report Unreachable while off, so an idle console with its TV off shows Offline on the dashboard (locked status rule); confirm on the cafe's actual TV models.
- TVCT-04 stays pending: the model carries the state, the dashboard display and manual on/off buttons are plan 03.

## Threat Flags

None beyond the plan's threat model (T-04-06 to T-04-09 and T-04-15 mitigated and tested; no new endpoints).

## Self-Check: PASSED

All 9 created files exist; commits e677b57 and ade7626 exist; build 0 warnings, 560 tests green.
