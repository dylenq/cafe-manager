---
phase: 02-session-engine-pricing
plan: 03
subsystem: session-lifecycle
tags: [efcore, sqlite, blazor, audit-trail, pricing, concurrency]
requires:
  - phase: 02-session-engine-pricing
    provides: Session/segment/event entities, PricingEngine, SessionService start+stop, rate rules and settings (plans 01, 02)
provides:
  - SessionService StartOpenEndedAsync, PauseAsync, ResumeAsync, ExtendAsync, MoveAsync, StopAsync (final), GetStopQuoteAsync
  - SessionCharges shared open-ended pricing (stop, stop quote, dashboard estimate)
  - SessionHistoryService and the /sessions audit-trail page with station filter
affects: [02-04, 02-05]
actuals:
  tokens: 40000
  tasks: 3
  commits: 3
commits: 3
plan_head_before: 7ace63dc399cdca8949737ba9bde68a5a09cef28
plan_head_after: c8d9c1af4aff2a37bc836e917a3496d4e31335f9
tech-stack:
  added: []
  patterns:
    - One private MutateAsync skeleton: fresh context, load with segments and station type, action, Version++, single SaveChanges, stale and unique-index conflicts mapped to Validation, notifier after commit
    - Test seam SessionService.BeforeSave (internal, InternalsVisibleTo Cafe.Tests) to simulate a stale write
key-files:
  created:
    - src/Cafe.Server/Sessions/{SessionCharges,SessionHistoryService}.cs
    - src/Cafe.Server/Components/Pages/Sessions.razor
    - tests/Cafe.Tests/{SessionLifecycleTests,SessionExtendMoveTests,SessionHistoryTests}.cs
    - tests/Cafe.Tests/Infrastructure/SessionTestBase.cs
  modified:
    - src/Cafe.Server/Sessions/SessionService.cs
    - src/Cafe.Server/Dashboard/DashboardService.cs
    - src/Cafe.Server/Components/Layout/NavMenu.razor
    - src/Cafe.Server/Program.cs
    - src/Cafe.Server/Cafe.Server.csproj
key-decisions:
  - "ExtendAsync returns the extension quote (not the new total); the new total lives in Session.ChargedAmount."
  - "Extend from Grace returns to Active only when remaining is above zero after the extension; otherwise it stays in Grace."
  - "Open-ended pricing lives in a singleton SessionCharges used by StopAsync, GetStopQuoteAsync and DashboardService so card estimate equals the stop charge."
  - "Station filter on /sessions matches current station, any segment station or any event station."
requirements-completed: [SESS-02, SESS-03, SESS-04, SESS-05, SESS-10]  # SESS-06 stays partial: stop preview exists at service level, the card confirmation UI is plan 05
status: complete
---

# Phase 2 Plan 03: Session lifecycle and audit trail Summary

Full session lifecycle at the service level (open-ended start, pause/resume, extend including from Grace, move, stop with per-segment open-ended billing, stop preview) with every action audited in the same transaction, plus a protected /sessions page showing the 200 newest sessions with an expandable event trail.

## Accomplishments

- `SessionService` rebuilt around one `MutateAsync` skeleton. Start validation is shared between prepaid and open-ended.
- Pause closes the open segment, Resume opens one on the current station; paused time is excluded from remaining time and from open-ended charges (tests: 1h paused on prepaid keeps remaining; open-ended 10+5 active minutes bills 1500.00).
- Extend (prepaid only, 1..1440, Active/Paused/Grace) quotes from the planned end at the current station's type; a happy-hour rule starting at the planned end is used for the extension. Grace extend reopens a segment, clears `GraceStartedAtUtc`, and remaining is exactly the extension.
- Move (Active/Paused) rejects busy, disabled, unknown, same, Grace and Ended; the filtered unique index backs the race (concurrent moves to one target: exactly one wins). Notifier fires for both stations.
- Stop from Active/Paused/Grace; open-ended charge is priced per segment at that segment's station type (move across types bills each at its own rate). Second stop fails with no new event.
- `GetStopQuoteAsync` reads only. Dashboard open-ended cards show the running estimate and stay flat while paused.
- `/sessions`: station filter, mode, start, end, reason/live state, active h:mm:ss, charge, expandable events; times shown in the cafe time zone; Razor encoding only.
- Tests: 260 total pass (226 before, 34 new across lifecycle, extend/move, history).

## Task Commits

1. Task 1 (open-ended start, pause, resume, stop billing, quote, concurrency): ffd173b
2. Task 2 (extend and move): 8395a42
3. Task 3 (history service, /sessions page, nav): c8d9c1a

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Stale-write test needs a seam**
- **Found during:** Task 1
- **Issue:** the service creates its own context per call, so a test cannot bump Version between load and save.
- **Fix:** added an `internal Func<Task>? BeforeSave` hook on SessionService and `InternalsVisibleTo Cafe.Tests` in the server csproj.
- **Files:** src/Cafe.Server/Sessions/SessionService.cs, src/Cafe.Server/Cafe.Server.csproj
- **Commit:** ffd173b

**2. [Rule 3 - Blocking] Shared test setup**
- **Issue:** three new test classes need the same seeding, clock parking and helpers.
- **Fix:** added `tests/Cafe.Tests/Infrastructure/SessionTestBase.cs` (not in the plan's file list).
- **Commit:** ffd173b

Dashboard open-ended estimate required `DashboardService` to take `SessionCharges` and `PricingConfigService` (constructor change, DI-resolved; no direct constructions existed).

## Authentication gates

None.

## Known Stubs

None.

## Threat Flags

None beyond the plan's threat model. T-02-11 to T-02-17 mitigations are implemented and tested: argument and target re-validation, unique-index move race, segment-based paused accounting, Version conflict as Validation failure, Razor-encoded details (no MarkupString), `[Authorize]` with anonymous redirect test, event in the same SaveChanges per mutation.

## Human checks pending

- Optional manual smoke of /sessions in a browser (row Events toggle, station filter) after plan 05 adds card controls that create sessions interactively. Not run (no server started); page render is covered by WebApplicationFactory tests, the expand/filter interactivity is not.

## Self-Check: PASSED

- Files exist: SessionCharges.cs, SessionHistoryService.cs, Sessions.razor, three test files, SessionTestBase.cs.
- Commits ffd173b, 8395a42, c8d9c1a exist; measured count 3 from ledger base.
- `dotnet build Cafe.sln` 0 warnings/0 errors; `dotnet test Cafe.sln` 260/260 pass.
- No DateTime/DateTimeOffset Now/UtcNow in src (excluding Migrations); no MarkupString in Sessions.razor.
