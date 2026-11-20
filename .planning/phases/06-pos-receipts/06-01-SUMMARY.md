---
phase: 06-pos-receipts
plan: 01
subsystem: pos-payments-receipts
tags: [ef-migration, sqlite, settlement, allocations, receipts, blazor]
requires:
  - phase: 05-customers-packages
    provides: Payment, PackageSale, Customer, CustomerTestBase
provides:
  - Product, Order, OrderLine, PaymentAllocation entities and the single AddPos migration
  - SettlementService (PreviewAsync, SettleAsync), AllocationPlanner, OutstandingCalculator, SettlementLock
  - CheckoutService.StopAndSettleAsync, OrderService.CreateAsync, ProductService.CreateAsync
  - ReceiptBuilder (pure), ReceiptService.GetAsync, /receipts/{paymentId} page, ReceiptLayout
  - AddPosServices and AddReceiptServices registration hooks, PosTestBase
affects: [06-02, 06-03, 06-04, 06-05]
tech-stack:
  added: []
  patterns: [allocations as source of truth, one SaveChanges per settlement, waterfall allocation, singleton semaphore lock]
key-files:
  created:
    - src/Cafe.Server/Data/PosEnums.cs
    - src/Cafe.Server/Data/Product.cs
    - src/Cafe.Server/Data/Order.cs
    - src/Cafe.Server/Data/OrderLine.cs
    - src/Cafe.Server/Data/PaymentAllocation.cs
    - src/Cafe.Server/Data/Migrations/20261120071317_AddPos.cs
    - src/Cafe.Server/Payments/ (SettlementLock, PaymentModels, AllocationPlanner, OutstandingCalculator, SettlementService, CheckoutService, PosServiceCollectionExtensions)
    - src/Cafe.Server/Products/ (ProductInput, ProductService)
    - src/Cafe.Server/Orders/ (OrderDraft, OrderService)
    - src/Cafe.Server/Receipts/ (ReceiptModels, ReceiptBuilder, ReceiptService, ReceiptServiceCollectionExtensions)
    - src/Cafe.Server/Components/Layout/ReceiptLayout.razor
    - src/Cafe.Server/Components/Pages/Receipt.razor
    - src/Cafe.Server/Components/Pages/Receipt.razor.css
    - tests/Cafe.Tests/Infrastructure/PosTestBase.cs
    - tests/Cafe.Tests/PosTracerTests.cs
    - tests/Cafe.Tests/PosMigrationTests.cs
    - tests/Cafe.Tests/AllocationPlannerTests.cs
  modified:
    - src/Cafe.Server/Data/Payment.cs
    - src/Cafe.Server/Data/CafeDbContext.cs
    - src/Cafe.Server/Program.cs
    - tests/Cafe.Tests/DataModelTests.cs
key-decisions:
  - "CheckoutService treats an empty tender list (or nothing owing) as 'Stop, pay later': the stop succeeds, Settlement and PaymentError are both null."
  - "A refused payment after a successful stop returns Success with PaymentError set; the session stays ended and items stay outstanding."
  - "Session receipt target carries the station name as title and 'H:MM, Mode' (sum of closed segments) as detail, with no item rows."
  - "Phase 5 PackageService.SellAsync is untouched here; plan 02 reworks it to write its allocation."
requirements-completed: []
duration: ~40 min
completed: 2026-11-20
status: complete
actuals:
  tasks: 2
  commits: 3
plan_head_before: 19160efc6470f21a63364169a2866ae4d213bc78
plan_head_after: 42cdbb82a2682297f0948e3c9deda95387cb0395
commits: 3
---

# Phase 6 Plan 01: POS Tracer Foundation Summary

One thin path through every layer: product, order on an active session, Stop and settle into one Payment with allocations written in a single SaveChanges, and a browser receipt page, on top of the whole Phase 6 schema in one EF migration.

## Tasks

| Task | Name | Commit |
| ---- | ---- | ------ |
| 1 | Tracer: product, order on a session, Stop and settle, receipt page | 62ceccb |
| 2 | Migration data move, planner and outstanding tests | d3f32c0, 42cdbb8 (test hardening) |

## What was built
- Schema: Products (NOCASE unique name), Orders, OrderLines (immutable snapshots), PaymentAllocations (polymorphic target, no FK), Payment.OrderId and PrimaryPaymentId. Generated with `dotnet tool run dotnet-ef` as `AddPos`; Up additionally copies Phase 5 payments into allocations (package sale in preference to session) and inserts `Pos:BillingStartUtc`. `has-pending-model-changes` is clean.
- SettlementService: tenders validated, SettlementLock held, targets loaded fresh, outstanding recomputed in memory, ExpectedDue mismatch returns "Amounts changed — review the new total.", applied above due is rejected, then Payments + allocations + order Paid marks in one SaveChanges. Cash change is returned, never stored.
- AllocationPlanner waterfall (cash 5000 + card 3000 over 6000 and 2000 gives T1 5000; T1 1000 + T2 2000).
- ReceiptBuilder is pure; ReceiptService resolves the root payment (split checkout = one receipt) and loads session, order and package targets, header settings and the cafe time zone.
- No NuGet package added.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Migration test failed in full-suite runs**
- **Found during:** Task 2 verification
- **Issue:** Raw inserts with dangling PackageSaleId/SessionId passed alone but failed with FK constraint errors when the Payments table was rebuilt in a full run.
- **Fix:** Seed real parent rows via EF (those tables are unchanged by AddPos), then insert Payments by raw SQL referencing real ids.
- **Files modified:** tests/Cafe.Tests/PosMigrationTests.cs
- **Commit:** 42cdbb8

Otherwise the plan was executed as written; the real Phase 5 names matched the plan.

## Issues / follow-ups
- Phase 5 `PackageService.SellAsync` still writes a Payment without an allocation, so a package sold between this plan and plan 02 would show as unpaid. Plan 02 (SellPaidAsync) fixes it; do not ship between the two.
- `Pos:BillingStartUtc` is seeded but nothing reads it yet (later plan: open-tabs unpaid-session listing).

## Human checks pending
- Open `/receipts/{id}` in a browser and look at the narrow layout and print preview (CSS only; auto-print is plan 05).

## Known Stubs
None.

## Threat Flags
None beyond the plan's threat model (T-06-01..05 mitigated: lock, single SaveChanges, insert-only rows, authorised receipt route with anonymous redirect test, migration test).

## Verification
- `dotnet build Cafe.sln` clean; `dotnet test Cafe.sln`: 759 passed, 0 failed (run twice).

## Self-Check: PASSED
