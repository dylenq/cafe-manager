---
phase: 04-samsung-tv-control
plan: 03
subsystem: ui
tags: [blazor, tv-pairing, dashboard, settings]
requires:
  - phase: 04-samsung-tv-control
    provides: TvPairingService, TvControlService, TvStateCache, TvSettingsService (plans 01, 02)
provides:
  - TvPairControl (Pair TV, Test, Paired badge) on /stations
  - TvControls (state indicator, TV On/Off with confirm during a session) on console cards
  - Live dashboard refresh and warning notices from TvStateCache
  - TvSettingsPanel (power off on stop toggle, help list) on /settings
affects: [04-04]
status: complete
commits: 3
plan_head_before: 4921c0839d55f183836118123c7d74d22eb005d1
plan_head_after: 447feaeba2fc4bea650e0057c8a74328995ba833
actuals:
  tokens: 14000
  tasks: 3
  commits: 3
tech-stack:
  added: []
  patterns:
    - "Components receive only a Paired bool, never the token"
    - "Home subscribes to TvStateCache.Changed/NoticeRaised after first interactive render and unsubscribes in DisposeAsync"
key-files:
  created:
    - src/Cafe.Server/Components/Shared/TvPairControl.razor
    - src/Cafe.Server/Components/Shared/TvControls.razor
    - src/Cafe.Server/Components/Shared/TvSettingsPanel.razor
    - tests/Cafe.Tests/TvUiTests.cs
  modified:
    - src/Cafe.Server/Components/Pages/Stations.razor
    - src/Cafe.Server/Components/Shared/StationCard.razor
    - src/Cafe.Server/Components/Pages/Home.razor
    - src/Cafe.Server/Components/Pages/Settings.razor
    - tests/Cafe.Tests/Infrastructure/FakeSamsungTv.cs
key-decisions:
  - "Home Notice record gains an optional Warning flag so TV failures render as alert-warning; existing notices unchanged"
requirements-completed: [TVCT-01, TVCT-04]
---

# Phase 4 Plan 3: TV UI Summary

Staff can pair and test a console TV from /stations, see each console card's TV state with TV On/Off buttons (confirmation while a session runs) that update live with failure notices, and control power-off-on-stop from a new Samsung TVs section on /settings.

## Tasks

1. Pair TV and Test controls (8babbdc): TvPairControl with spinner and busy guard, wired into Stations rows for consoles with a TV IP.
2. Dashboard card controls (8467ccc): TvControls in StationCard after the PC menu; Home subscribes to the state cache and notices.
3. Settings panel and tests (447feae): TvSettingsPanel after AgentsPanel; TvUiTests (18 tests) cover all three tasks. The test file was committed with task 3, so the task 1 and 2 commits do not include their tests.

Build: 0 warnings. Full suite: 578 passed, five consecutive green runs after the fix below. No csproj, migration or package changes. StationCard stop-confirm binding and PcCommandMenu are untouched (edits were additive).

## Deviations from Plan

**1. [Rule 3 - Blocking] FakeSamsungTv bound the server's fixed port 5080.** The test output folder carries the server's appsettings.json (Kestrel Http endpoint 0.0.0.0:5080), which overrode the fake's `urls` setting. Once a second test class (TvUiTests) used the fake in parallel with TvTracerTests, runs failed intermittently with "address already in use". Fixed by setting `Kestrel:Endpoints:Http:Url` to `http://127.0.0.1:0` in the fake. Files: tests/Cafe.Tests/Infrastructure/FakeSamsungTv.cs. Commit 447feae.

## Known Stubs

None.

## Human checks pending

- Real browser and real Samsung TV: pair from /stations and accept the prompt on the TV; confirm the spinner and result update live.
- Confirm the TV indicator and TV On/Off buttons on a console card; TV Off during a running session asks for confirmation.
- Start and stop a console session: TV wakes, then powers off; with the Settings toggle off the TV stays on after stop.
- Confirm Wake-on-LAN works with "Power On with Mobile" enabled on the cafe's TV models.

## Threat Flags

None. No new endpoints; controls live on [Authorize] pages (anonymous redirect tests for /, /stations, /settings); token absent from /stations and / HTML (tested); no raw markup rendering in the new components.

## Self-Check: PASSED

All created files exist; commits 8babbdc, 8467ccc, 447feae exist; build 0 warnings, 578 tests green.
