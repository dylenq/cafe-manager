---
phase: 05-customers-packages
fixed_at: 2026-11-20T00:00:00Z
review_path: .planning/phases/05-customers-packages/05-REVIEW.md
iteration: 1
findings_in_scope: 6
fixed: 6
skipped: 0
status: all_fixed
---

# Phase 5: Code Review Fix Report

**Fixed at:** 2026-11-20
**Source review:** .planning/phases/05-customers-packages/05-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 6 (CR-01, WR-01..WR-04, plus IN-03 as requested)
- Fixed: 6
- Skipped: 0

Verification ran in the isolated worktree (branch `worktree-agent-aba94ce2ace97a44d`): `dotnet build Cafe.sln` clean, `dotnet test Cafe.sln` 750 passed, 0 failed. No EF migration was added.

## Fixed Issues

### CR-01: Refund after a paid extension credits balance minutes the customer paid cash for

**Files modified:** `src/Cafe.Server/Sessions/SessionService.cs`, `tests/Cafe.Tests/BalanceSessionLifecycleTests.cs`
**Commit:** d65a1be
**Applied fix:** `RefundableMinutesAsync` now returns `max(0, drawnFromBalance - ceil(activeSeconds/60))`. Decision: balance-funded minutes are consumed first, paid-extension minutes last, so only the unconsumed part of the balance draw is refundable. Regression tests: 60 balance + 30 paid, used 70 gives refund 0; used 20 gives refund 40. An existing mixed-history test expected 65 and was updated to 55 (drawn 80 minus used 25), which is the new rule. Because this is a logic change it should be confirmed by a human (status: fixed, requires human verification).

### WR-01: CustomerDetail can show customer A's data while operating on customer B

**Files modified:** `src/Cafe.Server/Components/Pages/CustomerDetail.razor`
**Commit:** 26aeb21
**Applied fix:** When `Id` changes, `detail`, messages and form state are cleared and the page shows loading. A failed load also nulls `detail`. Sell and Adjust buttons (and their handlers) require `detail.Customer.Id == Id`.

### WR-02: Station type delete reports a wrong reason for package/ledger references

**Files modified:** `src/Cafe.Server/Stations/StationTypeService.cs`, `tests/Cafe.Tests/StationTypeServiceTests.cs`
**Commit:** 5177225
**Applied fix:** `DeleteAsync` pre-checks `TimePackages` ("used by N time package(s)") and `CustomerMinuteLedger` ("customers have prepaid minutes or history recorded against it"). The post-FK-failure branch no longer falls back to a fake "1 rate rule" and uses the same helper. Two tests added.

### WR-03: ExtendFromBalance ignores inactive customers

**Files modified:** `src/Cafe.Server/Sessions/SessionService.cs`, `tests/Cafe.Tests/BalanceSessionLifecycleTests.cs`
**Commit:** f02d6fa
**Applied fix:** Decision: reject, consistent with start. `ExtendFromBalanceAsync` calls `CustomerService.LoadLinkableAsync` and fails with the inactive message before debiting. Test verifies nothing changes (purchased minutes, ledger, balance).

### WR-04: Package price decimals and sale price drift

**Files modified:** `src/Cafe.Server/Customers/TimePackageInput.cs`, `src/Cafe.Server/Customers/PackageService.cs`, `src/Cafe.Server/Components/Pages/CustomerDetail.razor`, `tests/Cafe.Tests/PackageServiceTests.cs`
**Commit:** 3e97b50
**Applied fix:** `TimePackageInput` implements `IValidatableObject` rejecting prices with more than 2 decimals (validation only, no migration). `SellAsync` gained an optional `decimal? expectedPrice` parameter placed before the `CancellationToken` (existing callers unchanged) and fails with "Price changed — review the package and try again." on mismatch. The customer page passes the displayed price. Tests added. Note for Phase 6: the new parameter must be carried through if `SellAsync` delegates to `SellPaidAsync`.

### IN-03: DbUpdateException swallowed without logging (requested)

**Files modified:** `src/Cafe.Server/Customers/PackageService.cs`, `src/Cafe.Server/Customers/BalanceService.cs`
**Commit:** bf24cce
**Applied fix:** Added an optional trailing `ILogger<T>? logger = null` constructor parameter to both services (DI supplies it; existing `new PackageService(...)` test calls still compile) and log the exception before returning the failure.

## Skipped Issues

None. IN-01, IN-02 and IN-04 were out of the requested scope.

---

_Fixed: 2026-11-20_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
