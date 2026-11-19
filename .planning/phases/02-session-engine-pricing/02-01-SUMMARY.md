---
phase: 02-session-engine-pricing
plan: 01
subsystem: sessions-pricing
tags: [efcore, sqlite, blazor, timeprovider, pricing, dst]
requires:
  - phase: 01-foundation
    provides: stations, station types, dashboard, service + ServiceResult pattern, test factory
provides:
  - Session, SessionSegment, SessionEvent, RateRule entities and the single phase-2 migration
  - Pure PricingEngine with per-window breakdown (rule windows, local midnight, DST)
  - SessionService.StartPrepaidAsync / StopAsync, SessionNotifier, SessionClock, session settings read side
  - Session-aware dashboard status and a working Start/Stop card with live countdown
  - FakeTimeProvider on CafeWebApplicationFactory
affects: [02-02, 02-03, 02-04, 02-05, 03-pc-agent]
actuals:
  tokens: 20000
  tasks: 2
  commits: 2
plan_head_before: 506dc30a78c40cc312b7b4e67a6cb8ea1bbdc76a
plan_head_after: f455afff38c2b0aab9078e28fe91bb691f1b0fa0
tech-stack:
  added: [Microsoft.Extensions.TimeProvider.Testing 10.11.0 (tests only)]
  patterns:
    - IDbContextFactory service returning ServiceResult, notifier raised after commit
    - Session.Version concurrency token plus filtered unique index for one live session per station
    - Default interface method to extend IStationStatusProvider without breaking implementers
key-files:
  created:
    - src/Cafe.Server/Data/{SessionEnums,Session,SessionSegment,SessionEvent,RateRule}.cs
    - src/Cafe.Server/Data/Migrations/20261119191837_AddSessionsAndRateRules.cs
    - src/Cafe.Server/Pricing/{PricingModels,PricingEngine,PricingConfigService,CafeTimeZone}.cs
    - src/Cafe.Server/Sessions/{SessionClock,SessionNotifier,SessionSettings,SessionSettingsService,SessionService,SessionStatusMapper}.cs
    - src/Cafe.Server/Dashboard/SessionAwareStatusProvider.cs
    - tests/Cafe.Tests/{SessionTracerTests,PricingEngineTests}.cs
  modified:
    - src/Cafe.Server/Data/CafeDbContext.cs
    - src/Cafe.Server/Dashboard/{DashboardService,IStationStatusProvider,StationCardModel}.cs
    - src/Cafe.Server/Components/Shared/StationCard.razor
    - src/Cafe.Server/Components/Pages/{Home,Login}.razor
    - src/Cafe.Server/Program.cs
    - src/Cafe.Server/Stations/ServiceResult.cs
    - tests/Cafe.Tests/Infrastructure/CafeWebApplicationFactory.cs
key-decisions:
  - "Adjacent pricing slices with the same type, rate and rule are merged into one window, so a no-rule span has a single window even across midnight."
  - "Round-up pads the last interval by (60 - remainder) seconds; the plan text said 'the remainder', which would not reach a whole minute."
  - "DST gap times produce no cut of their own; offset transitions are detected by sampling and bisecting and are always a cut, which is the instant after the gap."
  - "FakeTimeProvider starts at the real current instant (whole seconds), not a fixed date, so AuthTests cookie-expiry assertions against the real clock keep passing."
requirements-completed: [PRIC-01]  # SESS-01, SESS-06, SESS-07 are only partly delivered (fixed 60 min, prepaid-only stop, no open-ended); completed by plans 03 and 05
---

# Phase 2 Plan 01: Tracer, data model and PricingEngine Summary

Prepaid sessions run end to end from a dashboard card (start 60 min, live countdown, stop with final charge) on a TimeProvider-driven clock, backed by the full phase-2 schema in one migration and a pure, DST-correct PricingEngine proven by 46 unit cases.

## Accomplishments

- One migration `AddSessionsAndRateRules` creates Session, SessionSegment, SessionEvent and RateRule, including the filtered unique index `IX_Sessions_StationId_Active` (`"Status" <> 3`). Later plans add no migrations.
- `PricingEngine.Price` is static and pure: splits at rule boundaries, local midnights and DST transitions, picks the highest-priority matching rule (ties to lowest Id), bills by the second, rounds up to a whole minute, then to 2 decimals AwayFromZero.
- `SessionService.StartPrepaidAsync` validates 1..1440 minutes, station exists and enabled, no live session (code check plus unique-violation catch), and writes session, open segment and Started event in one SaveChanges. `StopAsync` closes the segment, ends the session, writes the Stopped event and maps concurrency conflicts to a friendly failure.
- Dashboard is session-aware: `SessionAwareStatusProvider` lets a session override Offline; `StationCard` shows the countdown, charge and Start/Stop; `Home` refreshes on `SessionNotifier.Changed` and re-renders every second.
- Tests: 181 total pass (114 pre-existing, 21 new tracer/clock/mapper/settings tests, 46 pricing cases).

## Task Commits

1. Task 1 (tracer): f431be4
2. Task 2 (PricingEngine matrix): f455aff

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] ServiceResult<T> had no NotFound factory**
- **Found during:** Task 1 (StartPrepaid/Stop must return NotFound)
- **Fix:** added `ServiceResult<T>.NotFound`
- **Files:** src/Cafe.Server/Stations/ServiceResult.cs
- **Commit:** f431be4

**2. [Rule 1 - Bug] Login.razor read DateTimeOffset.UtcNow directly**
- **Found during:** Task 1 (acceptance grep for any DateTime/UtcNow in src returned a hit; violates the TimeProvider rule)
- **Fix:** injected TimeProvider. To keep the existing AuthTests cookie-expiry check valid, the test factory clock starts at the real current instant instead of a fixed 2026-03-04 date.
- **Files:** src/Cafe.Server/Components/Pages/Login.razor, tests/Cafe.Tests/Infrastructure/CafeWebApplicationFactory.cs
- **Commit:** f431be4

**3. [Rule 1 - Bug] Round-up padding amount**
- **Issue:** plan said extend the last interval "by the remainder"; that does not reach the next whole minute.
- **Fix:** pad by `60 - remainder`.
- **Commit:** f431be4

**4. [Rule 3 - Blocking] TimeProvider.Testing resolved to 11.0.0**
- **Issue:** `dotnet add package` picked 11.0.0, outside the 10.0.x constraint.
- **Fix:** pinned to 10.11.0, the latest 10.x available. Test project only; the server csproj gained no package.
- **Commit:** f431be4

## Authentication gates

None.

## Known Stubs

None. The card exposes only a fixed "Start 60 min" button by design; the full picker, pause, extend and move controls are plan 05 per the plan.

## Threat Flags

None beyond the plan's threat model. T-02-01 through T-02-03, T-02-05 and T-02-06 mitigations are implemented and covered by tests (minutes range, unique index, Version token, events in the same transaction, notifier try/catch).

## Human checks pending

- Optional manual smoke: `dotnet run --project src/Cafe.Server`, create a type and station, click "Start 60 min", watch the countdown tick and the pulsing refresh, then Stop. Not run (no server was started).

## Self-Check: PASSED

- Migration, entities, engine, service and test files exist; commits f431be4 and f455aff exist.
- `dotnet build Cafe.sln` has 0 errors; `dotnet test Cafe.sln` passes 181/181.
- No `DateTime.Now/UtcNow` or `DateTimeOffset.Now/UtcNow` in src (excluding Migrations); PricingEngine has no DB, clock or logger references.
