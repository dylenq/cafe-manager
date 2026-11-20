---
phase: 05-customers-packages
plan: 02
subsystem: customers-balance
tags: [customers, packages, ledger, services, search]
requires:
  - phase: 05-customers-packages
    provides: plan 01 schema, BalanceLock, BalanceService, minimal CustomerService/PackageService
provides:
  - CustomerService update, soft delete, search (50-row cap), phone rules, GetDetailAsync
  - PackageService list/get/update/deactivate/guarded delete and hardened SellAsync
  - BalanceService.GetBalanceAsync and AdjustAsync
  - CustomerDetail read model
affects: [05-03, 05-04, 05-05]
tech-stack:
  added: []
  patterns: [in-memory Unicode-safe search, field-error ServiceResult, shared static bucket sum]
key-files:
  created:
    - src/Cafe.Server/Customers/CustomerDetail.cs
    - tests/Cafe.Tests/CustomerServiceTests.cs
    - tests/Cafe.Tests/PackageServiceTests.cs
    - tests/Cafe.Tests/BalanceServiceTests.cs
  modified:
    - src/Cafe.Server/Customers/CustomerService.cs
    - src/Cafe.Server/Customers/PackageService.cs
    - src/Cafe.Server/Customers/BalanceService.cs
    - tests/Cafe.Tests/CustomerBalanceTracerTests.cs
key-decisions:
  - "Customer search filters in memory with invariant IgnoreCase IndexOf, because SQLite LIKE only folds ASCII."
  - "A failed SellAsync save returns a failure result instead of throwing; nothing is written."
  - "AdjustAsync allows inactive customers (staff correction) but never lets a bucket go below zero."
requirements-completed: []
status: complete
commits: 3
plan_head_before: f6ed4e39fb1dd787efa3b5f87402cef629efc762
plan_head_after: e53713cfa484753ad6498c7b7710b981d6eb82a9
actuals:
  tasks: 3
  commits: 3
duration: ~30 min
completed: 2026-11-20
---

# Phase 5 Plan 02: Customer, package and balance services Summary

Complete customer/package/balance domain services with tests: digits-only unique phones, Unicode-safe 50-row search, package deactivate and guarded delete, atomic sale that reports failures, never-negative manual adjustment, and the customer detail read model.

## Commits

- 6968844 feat(05-02): customer update, soft delete, search and phone rules
- 0f0daab feat(05-02): package CRUD, deactivate, guarded delete, hardened sale
- e53713c feat(05-02): balance summary, manual adjustment and customer detail model

## What was built

- `CustomerService`: `UpdateAsync`, `SetActiveAsync` (soft delete only), `SearchAsync`, `GetDetailAsync`, shared phone normaliser/validator (no digits or more than 20 digits is a Phone field error; conflicts say "inactive" when the holder is inactive; unique race on save becomes a Phone error).
- `PackageService`: `ListAsync`, `GetAsync`, `UpdateAsync`, `SetActiveAsync`, `DeleteAsync` (blocked once sold), duplicate-name and station-type checks as field errors, `SellAsync` rejects deactivated packages ("no longer on sale") and returns a failure on a failed save.
- `BalanceService`: `GetBalanceAsync` (non-zero buckets, any-type first, total) and `AdjustAsync` (note required, delta 1..100000 in magnitude, rejects going below zero, serialised via `BalanceLock`). Constructor now also takes `TimeProvider` and `BalanceLock` (resolved by DI; no manual construction existed).
- `CustomerDetail.cs`: `CustomerDetail`, `BalanceSummary`, `BalanceBucketRow`, `SaleRow`, `LedgerRow`, `RecentSessionRow`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Plan 01 test asserted SellAsync throws on a failed save**
- **Found during:** Task 2
- **Issue:** `Sale_rolls_back_atomically_when_the_save_fails` expected a `DbUpdateException`; plan 02 requires SellAsync to return a failure.
- **Fix:** Test now asserts `Success == false`; the zero-rows assertions are unchanged.
- **Files modified:** tests/Cafe.Tests/CustomerBalanceTracerTests.cs
- **Commit:** 0f0daab

No other deviations. No migrations, no Razor or SessionService changes.

## Known Stubs

None.

## Requirements

CUST-01..CUST-04 server behaviour is done, but the pages (plan 05) are still missing, so none were marked complete in REQUIREMENTS.md.

## Verification

`dotnet build Cafe.sln` clean (0 warnings). `dotnet test Cafe.sln`: 659 passed on the second run. The first run had two failures outside this plan's code (SessionTickerTests and TvSessionSinkTests, both timing-sensitive); they passed on rerun.

## Human checks pending

None (no UI in this plan).

## Self-Check: PASSED

All created files exist, commits 6968844, 0f0daab and e53713c are in the log, full suite green.
