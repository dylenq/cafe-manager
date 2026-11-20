---
phase: 05-customers-packages
plan: 04
subsystem: customers-balance
tags: [sessions, ledger, balance, refund]
requires:
  - phase: 05-01
    provides: BalanceService, StartFromBalanceAsync, early-stop refund
  - phase: 05-03
    provides: member snapshot pricing for paid extension
provides:
  - SessionService.ExtendFromBalanceAsync and InsufficientBalancePrefix
  - StopQuote.RefundMinutes (stop-quote refund preview)
  - Balance move restriction (type-specific minutes stay on their station type)
  - BalanceService.NetDrawnByBucketAsync
affects: [05-05]
key-files:
  created:
    - tests/Cafe.Tests/BalanceSessionLifecycleTests.cs
  modified:
    - src/Cafe.Server/Sessions/SessionService.cs
    - src/Cafe.Server/Customers/BalanceService.cs
key-decisions:
  - "Refund = min(unused whole minutes, minutes this session net-drew from balance); the same private method serves StopAsync and the stop quote."
  - "ExtendAsync and ExtendFromBalanceAsync share one private ApplyExtension helper for revival, event and signal."
  - "ExtendFromBalanceAsync takes BalanceLock around the whole mutation so concurrent extends cannot overdraw."
requirements-completed: [CUST-05]
status: complete
commits: 2
plan_head_before: a1dfa0b170f75e0dd47548c7b764ebcd2f0c97c6
plan_head_after: e689a901657fbcd5f2d5259ec2ad42af0ed34887
actuals:
  tokens: 30000
  tasks: 2
  commits: 2
duration: ~25 min
completed: 2026-11-20
---

# Phase 5 Plan 04: Balance session lifecycle Summary

Balance sessions now refund, extend, move and expire without leaking minutes: refunds are capped at what the session drew, extension can debit more balance (with a paid fallback), and the stop quote previews the refund.

## Commits

- cca5af9 feat(05-04): balance refund hardening, extend from balance, stop-quote refund, move rule
- e689a90 test(05-04): balance lifecycle regression for signals, ledger integrity and dashboard

## What was built

- One `RefundableMinutesAsync` (purchased minus ceil(active seconds/60), floored at 0, capped at net-drawn ledger minutes) used by `StopAsync` and `QuoteStopAsync`; `StopQuote` gained trailing `RefundMinutes = 0`.
- `ExtendFromBalanceAsync(sessionId, minutes)` returns usable minutes left; fails with `Not enough balance: N min available.` (prefix `SessionService.InsufficientBalancePrefix`) and changes nothing; revives Grace; leaves ChargedAmount alone.
- `MoveAsync` refuses a Balance session that drew a type-specific bucket when the target is another station type.
- 29 lifecycle tests (early stop, mixed buckets, pause, expiry, grace stop, double stop, stale quote, quote preview, extend, concurrency, paid fallback, move, signals, failing sink, ledger integrity, racing starts, dashboard).

## Deviations from Plan

None - plan executed as written. No production defect was exposed by the Task 2 regression tests, so SessionService was not touched in Task 2.

## Known Stubs

None.

## Threat Flags

None. T-05-09, T-05-10 and T-05-11 mitigations are implemented and tested.

## Human checks pending

None for this plan (no UI changes; plan 05 wires the UI to these APIs).

## Verification

`dotnet build Cafe.sln` clean; `dotnet test Cafe.sln` 707 passed, 0 failed.

## Self-Check: PASSED

Files and both commits verified present.
