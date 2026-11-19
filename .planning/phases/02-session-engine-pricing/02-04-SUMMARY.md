---
phase: 02-session-engine-pricing
plan: 04
subsystem: expiry-engine
tags: [background-service, timeprovider, efcore, sqlite, audit-trail]
requires:
  - phase: 02-session-engine-pricing
    provides: SessionService, SessionClock, SessionSettingsService, SessionNotifier, audit events (plans 01-03)
provides:
  - ExpiryEvaluator (warnings, expiry into Grace, grace-elapsed ending) behind IExpiryEvaluator
  - IExpiryActionHandler seam plus LoggingExpiryActionHandler default
  - SessionTicker BackgroundService on PeriodicTimer(1s, TimeProvider) with startup evaluation
  - Config flag Sessions:RunTicker (default true; the test factory sets false)
affects: [02-05, 03, 04]
actuals:
  tokens: 45000
  tasks: 2
  commits: 2
commits: 2
plan_head_before: 80440091afdc30353eb07aa54cd194b597b137e8
plan_head_after: 943c052a85567c331046ab8f7da6132c092541a8
tech-stack:
  added: []
  patterns:
    - Evaluator derives every instant from stored data (segments, GraceStartedAtUtc), never from the tick time
    - Session committed as Ended before handlers run, so handlers fire at most once; ExpiryActionFired is a second small save
    - Per-session fresh DbContext plus try/catch; per-handler try/catch; stale Version skips the session until the next tick
key-files:
  created:
    - src/Cafe.Server/Sessions/{ExpiryEvaluator,IExpiryActionHandler,LoggingExpiryActionHandler,SessionTicker}.cs
    - tests/Cafe.Tests/{ExpiryEvaluatorTests,SessionTickerTests}.cs
  modified:
    - src/Cafe.Server/Program.cs
    - tests/Cafe.Tests/Infrastructure/CafeWebApplicationFactory.cs
key-decisions:
  - "IExpiryEvaluator interface added so the ticker can be tested with a fake/failing evaluator; ExpiryEvaluator registered as itself and as the interface."
  - "SessionTicker.IsEnabled(IConfiguration) holds the Sessions:RunTicker default (true) so it is unit-testable."
  - "Warning dedup compares event Id with the latest Extended event Id (ordering by Id, not time)."
  - "Evaluator has an internal BeforeSave test seam, like SessionService, to simulate stale and failing saves."
status: complete
---

# Phase 2 Plan 04: Expiry Engine Summary

Per-second expiry engine: prepaid sessions raise one warning per threshold, move to Grace at the true expiry instant, then end as Expired after the wall-clock grace with every registered handler invoked exactly once, restart-safe and failure-isolated.

## What was built

- **ExpiryEvaluator** loads Active/Grace prepaid session ids, then processes each in its own DbContext and try/catch. Warnings use AtUtc = evaluation time; Expired uses open segment start plus remaining seconds at that segment start (true instant); GraceElapsed uses GraceStartedAtUtc + grace. A late or post-restart pass resolves Active -> Grace -> Ended in one call. ChargedAmount is untouched.
- **Handlers** run after the Ended commit, each in its own try/catch; ExpiryActionFired records the handler count and any failures.
- **SessionTicker** evaluates once at startup, then on each PeriodicTimer tick; loop-level catch keeps it alive.
- Notifier raised after each commit that changed a station.

## Deviations from Plan

None - plan executed as written. (Additive: `IExpiryEvaluator` interface and `SessionTicker.IsEnabled` helper, both required to meet the plan's own test behaviors.)

## Verification

- `dotnet build Cafe.sln`: 0 warnings, 0 errors. `dotnet test Cafe.sln`: 284 passed (24 new: 18 evaluator, 6 ticker). Ticker class run twice, stable.
- No `DateTime.Now/UtcNow` in src/Cafe.Server (excluding Migrations).

## Known Stubs

None. LoggingExpiryActionHandler is the intended default until Phase 3/4 handlers register.

## Threat Flags

None.

## Human checks pending

None for this plan. Desk alert UI for warnings/grace arrives in plan 05; end-to-end behavior against a real clock and a real device attaches in Phases 3/4.

## Requirements

SESS-09 delivered (grace then expiry action seam fires; real device actions attach in Phases 3/4) and marked complete. SESS-10 expiry events delivered (already complete). SESS-08 is data side only (alert UI is plan 05) and is not marked complete.

## Self-Check: PASSED

Files and commits 2586e82, 943c052 verified present.
