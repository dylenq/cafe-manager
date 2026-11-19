---
phase: 01-foundation
plan: 04
subsystem: dashboard
tags: [dotnet, blazor, efcore, dashboard, periodictimer]
requires: [01-03]
provides:
  - Cafe.Shared.StationStatus enum (Idle, InSession, Paused, Expiring, Offline)
  - IStationStatusProvider seam with DefaultStationStatusProvider (Idle/Offline rule)
  - DashboardService.GetCardsAsync returning ordered StationCardModel list
  - Dashboard grid at / with StationCard and StatusBadge components and 5-second self-refresh
affects: [phase-2-sessions, phase-3-agent, phase-4-tv]
tech-stack:
  added: []
  patterns: [swappable status provider via DI registration, PeriodicTimer refresh loop cancelled on IAsyncDisposable, NOCASE column collation gives case-insensitive SQL ordering]
key-files:
  created:
    - src/Cafe.Shared/StationStatus.cs
    - src/Cafe.Server/Dashboard/IStationStatusProvider.cs
    - src/Cafe.Server/Dashboard/DefaultStationStatusProvider.cs
    - src/Cafe.Server/Dashboard/StationCardModel.cs
    - src/Cafe.Server/Dashboard/DashboardService.cs
    - src/Cafe.Server/Components/Shared/StatusBadge.razor
    - src/Cafe.Server/Components/Shared/StationCard.razor
    - tests/Cafe.Tests/DashboardServiceTests.cs
    - tests/Cafe.Tests/DashboardPageTests.cs
  modified:
    - src/Cafe.Server/Program.cs
    - src/Cafe.Server/Components/Pages/Home.razor
    - src/Cafe.Server/Components/_Imports.razor
    - src/Cafe.Server/wwwroot/app.css
key-decisions:
  - "Case-insensitive name ordering relies on Station.Name's existing NOCASE collation, so ThenBy(Name) is ordered in SQL with no custom comparer"
  - "The refresh timer only starts when RendererInfo.IsInteractive, so prerender and static requests never leak a timer"
  - "ADMN-04 left Pending: the requirement text includes live in-session/paused/expiring status, which only has rendering support in Phase 1"
requirements-completed: []
duration: 15 min
completed: 2026-11-19
status: complete
commits: 3
plan_head_before: a062d9832c59df852454ab6da43b6ab604c2699e
plan_head_after: 04084a3af898ca6ff8323ac84afcb4012579589e
actuals:
  tokens: 9000
  tasks: 3
  commits: 3
---

# Phase 1 Plan 04: Live Dashboard Summary

Dashboard grid of enabled stations with Idle/Offline badges driven by a swappable IStationStatusProvider, auto-refreshing every 5 seconds behind login.

## Tasks

| Task | Name | Commit |
| ---- | ---- | ------ |
| 1 | StationStatus, provider seam, DashboardService + tests | 8d0a103 |
| 2 | Home.razor dashboard, StationCard, StatusBadge, CSS | 3247831 |
| 3 | DashboardPageTests (anonymous 302, empty state, cards, order) | 04084a3 |

## Verification

- `dotnet build Cafe.sln`: 0 warnings, 0 errors.
- `dotnet test Cafe.sln`: 98 passed, 0 failed (DashboardServiceTests 9, DashboardPageTests 4).
- DashboardService.cs contains no reference to InSession, Paused or Expiring.

## Deviations from Plan

None - plan executed exactly as written.

## Deferred / Manual

- The Task 3 human-check (LAN and phone access, one-column layout at phone width, rename appearing within about 5 seconds, staying logged in across refresh) was not run: this executor has no browser or phone. It remains an end-of-phase manual walkthrough for the user. The 5-second interactive refresh is not covered by automated tests (prerendered HTML only); this is the plan's `backstop` truth.
- ADMN-04 not marked complete in REQUIREMENTS.md because in-session/paused/expiring statuses are computed in later phases.

## Known Stubs

None.

## Threat Flags

None. T-01-13 mitigated: Home.razor inherits [Authorize], anonymous `/` returns 302 to /login (tested), cards expose no TvToken or MAC. No packages added (T-01-SC).

## Self-Check: PASSED

All created files exist; commits 8d0a103, 3247831, 04084a3 present.
