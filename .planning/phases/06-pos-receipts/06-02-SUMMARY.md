---
phase: 06-pos-receipts
plan: 02
subsystem: pos-payments
tags: [products, orders, settlement, open-tabs, tender, package-sale]
requires:
  - phase: 06-pos-receipts
    provides: plan 01 schema, SettlementService, AllocationPlanner
provides:
  - ProductService full CRUD, OrderService void and queries
  - OpenTabsService, TenderCalculator, PaymentHistoryService
  - CheckoutService.QuoteStopAndSettleAsync, SettlementService constants
  - PackageService.SellPaidAsync (Phase 5 SellAsync delegates to the same core)
affects: [06-04, 06-05]
key-files:
  created:
    - src/Cafe.Server/Orders/OpenTabsService.cs
    - src/Cafe.Server/Payments/TenderCalculator.cs
    - src/Cafe.Server/Payments/PaymentHistoryService.cs
    - tests/Cafe.Tests/ProductServiceTests.cs
    - tests/Cafe.Tests/OrderServiceTests.cs
    - tests/Cafe.Tests/SettlementServiceTests.cs
    - tests/Cafe.Tests/OpenTabsServiceTests.cs
    - tests/Cafe.Tests/TenderCalculatorTests.cs
    - tests/Cafe.Tests/PackagePaymentTests.cs
  modified:
    - src/Cafe.Server/Products/ProductService.cs
    - src/Cafe.Server/Orders/OrderService.cs
    - src/Cafe.Server/Payments/PaymentModels.cs
    - src/Cafe.Server/Payments/SettlementService.cs
    - src/Cafe.Server/Payments/CheckoutService.cs
    - src/Cafe.Server/Payments/PosServiceCollectionExtensions.cs
    - src/Cafe.Server/Customers/PackageService.cs
key-decisions:
  - "SettleAsync checks ExpectedDue before 'nothing to pay', so a second circuit paying an already-paid order sees the changed-amounts message."
  - "SellPaidAsync saves the sale and its allocations in two SaveChanges inside one transaction, because the allocation needs the new sale id and has no FK."
  - "A free (price 0) package still writes a zero payment with no allocation, keeping the Phase 5 behaviour."
  - "Open tabs show running sessions' orders under the session with ' - open session' in the header."
status: complete
actuals:
  tasks: 3
  commits: 3
plan_head_before: 6d5c0dc943ea5cf9430dd041b8fa7af310a3e05e
plan_head_after: 0e12ff0142b707113b048602c827e6afd59cb338
commits: 3
completed: 2026-11-20
---

# Phase 6 Plan 02: POS Domain Services Summary

The server side of POS-01..03 is finished and edge-tested: product CRUD, order void rules, open tabs, split/change/partial/concurrent settlement, the stop-and-settle quote, tender math, receipt history, and package sales through the payment path.

## Tasks

| Task | Name | Commit |
| ---- | ---- | ------ |
| 1 | Products and orders: CRUD, void rules, queries | 6124c6c |
| 2 | Settlement edges, open tabs, stop-settle quote, tender math, history | 223a7a2 |
| 3 | Package sale through the payment path | 0e12ff0 |

## Notes
- OrderService now takes SettlementLock (void is serialised with settle). DI supplies it.
- Phase 5 SellAsync keeps its signature, messages, expectedPrice check and logger; it now writes one allocation.
- No migration, no Receipts or Program.cs changes. No packages added.
- REQUIREMENTS.md not updated: POS-01..03 need the pages in plans 04/05 to be fully delivered.

## Deviations from Plan

None material. Small additions: `SettlementService.ValidateTenders` made internal and shared with PackageService; `OverDue` is an alias of `OverpayMessage`; a MaxLineAmount cap (100,000,000).

## Human checks pending
None for this plan (services only).

## Known Stubs
None.

## Threat Flags
None beyond the plan's threat model (T-06-06..10 mitigated and tested).

## Verification
`dotnet build Cafe.sln` clean (0 warnings); `dotnet test Cafe.sln`: 841 passed, 0 failed (one earlier run hit the known flaky TV timing test, passed on rerun).

## Self-Check: PASSED
