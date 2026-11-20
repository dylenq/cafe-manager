---
phase: 05-customers-packages
verified: 2026-11-20T00:00:00Z
status: passed
score: 4/4 must-haves verified
covered_files:
  - src/Cafe.Server/Customers/BalanceService.cs
  - src/Cafe.Server/Customers/PackageService.cs
  - src/Cafe.Server/Sessions/SessionService.cs
covered_digest: "v2:sha256:307ee8147a1f71390e8e9dc73a9563b8f9953e0d0830acbe21c656c476696757"
behavior_unverified: 0
---

# Phase 5: Customers & Packages Verification Report

**Phase Goal:** Staff can look up customers, apply member pricing, sell prepaid time packages, and start sessions that draw from a customer's balance
**Verified:** 2026-11-20
**Status:** passed
**Re-verification:** No, initial verification
**Evidence base:** code and tests read; the full `dotnet test Cafe.sln` run (1054/1054 passed) was supplied by the caller, no build was run here.

## Observable Truths

| # | Truth (ROADMAP SC) | Status | Evidence |
|---|---|---|---|
| 1 | Create/edit customers (name, phone, member, notes); find by name or phone | VERIFIED | `Customers/CustomerService.cs` (CRUD, phone normalized, `SearchAsync` name-contains or phone-digits-contains, `MaxSearchRows = 50`); pages `Customers.razor`, `CustomerDetail.razor` (global `[Authorize]` from `_Imports.razor`). Tests: `CustomerServiceTests`, `CustomerPagesTests`. |
| 2 | Define packages (minutes, price, optional station-type limit) and sell one: adds balance and records payment | VERIFIED | `PackageService` CRUD plus `SellAsync`/`SellPaidAsync` -> `SellCoreAsync`: PackageSale, Payment(s), allocations and `CustomerMinuteLedger` credit in one transaction, rolled back and logged on `DbUpdateException`; price snapshot and expected-price guard. `Packages.razor`. Tests: `PackageServiceTests`, `PackagePaymentTests`. |
| 3 | Start a session that draws from a customer's minute balance | VERIFIED | `SessionService.StartFromBalanceAsync` (usable = type bucket + any bucket, ledger debit in same save, `SessionMode.Balance`), `ExtendFromBalanceAsync`, early-stop refund via `RefundableMinutesAsync`/`RefundUnusedAsync`, move guard for type-specific balance. UI in `StationCard.razor` (`CustomerPicker`, Start from balance, Extend from balance). Tests: `BalanceSessionLifecycleTests`, `BalanceServiceTests`, `CustomerBalanceTracerTests`, `BalanceModeSweepTests`, `CustomerDashboardUiTests`. |
| 4 | Member session charged member rate; guest charged guest rate | VERIFIED | `PricingEngine.BaseRate(type, isMember)` = `MemberHourlyRate ?? HourlyRate`; `AudienceMatches` filters rules by `RateRuleAudience`; `Session.IsMemberPricing` snapshotted at start and passed into charge calculations (`SessionService` lines 85, 111, 327, 504, 553). Tests: `PricingEngineMemberTests`, `MemberSessionPricingTests`, `MemberPricingPagesTests`. |

**Score:** 4/4

## 05-REVIEW-FIX.md fixes (6/6 present)

| Finding | Evidence |
|---|---|
| CR-01 (d65a1be) | `RefundableMinutesAsync` caps refund at `max(0, netDrawn - used)`, shared by Stop and the stop quote |
| WR-01 (26aeb21) | `CustomerDetail.razor` `ShowsCurrentCustomer` (`detail.Customer.Id == Id`), stale detail dropped on navigation |
| WR-02 (5177225) | `StationTypeService` lines 156-162 count `TimePackages` and `CustomerMinuteLedger` references with specific messages |
| WR-03 (f02d6fa) | `ExtendFromBalanceAsync` runs `CustomerService.LoadLinkableAsync` and rejects inactive customers |
| WR-04 (3e97b50) | `TimePackageInput` rejects more than 2 decimals; `SellCoreAsync` rejects a changed price |
| IN-03 (bf24cce) | `logger?.LogError` on `DbUpdateException` in sale (and balance adjust) |

## Requirements Coverage

| Req | Status | Evidence |
|---|---|---|
| CUST-01 | SATISFIED | Truth 1 |
| CUST-02 | SATISFIED | Truth 1 (`SearchAsync`) |
| CUST-03 | SATISFIED | Truth 2 (`PackageService`, `/packages`) |
| CUST-04 | SATISFIED | Truth 2 (`SellCoreAsync`) |
| CUST-05 | SATISFIED | Truth 3 |
| CUST-06 | SATISFIED | `Session.CustomerId`, `CustomerPicker` in start dialogs, `LoadLinkableAsync` |
| PRIC-03 | SATISFIED | Truth 4 |

No orphaned requirements.

## Anti-Patterns

No TBD/FIXME/XXX in Phase 5 files; no stubs found. Note: Phase 6 later rerouted package sales through `SellPaidAsync` with allocations; the Phase 5 `SellAsync` entry point still shares that code path and is covered by tests.

## Human Verification

None required for the goal (all behaviour is service or Blazor-render covered). Optional visual check of the customer picker in a browser is cosmetic.

---
_Verified: 2026-11-20_
_Verifier: Claude (gsd-verifier)_
