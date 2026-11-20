---
phase: 05-customers-packages
reviewed: 2026-11-20T00:00:00Z
depth: standard
files_reviewed: 41
files_reviewed_list:
  - src/Cafe.Server/Customers/BalanceLock.cs
  - src/Cafe.Server/Customers/BalanceService.cs
  - src/Cafe.Server/Customers/CustomerDetail.cs
  - src/Cafe.Server/Customers/CustomerInput.cs
  - src/Cafe.Server/Customers/CustomerPick.cs
  - src/Cafe.Server/Customers/CustomerService.cs
  - src/Cafe.Server/Customers/PackageService.cs
  - src/Cafe.Server/Customers/PhoneDisplay.cs
  - src/Cafe.Server/Customers/TimePackageInput.cs
  - src/Cafe.Server/Sessions/SessionService.cs
  - src/Cafe.Server/Sessions/SessionCharges.cs
  - src/Cafe.Server/Sessions/ExpiryEvaluator.cs
  - src/Cafe.Server/Pricing/PricingEngine.cs
  - src/Cafe.Server/Pricing/RateRuleService.cs
  - src/Cafe.Server/Pricing/RateRuleInput.cs
  - src/Cafe.Server/Stations/StationTypeService.cs
  - src/Cafe.Server/Stations/StationTypeInput.cs
  - src/Cafe.Server/Dashboard/DashboardService.cs
  - src/Cafe.Server/Components/Shared/CustomerPicker.razor
  - src/Cafe.Server/Components/Shared/StationCard.razor
  - src/Cafe.Server/Components/Pages/Customers.razor
  - src/Cafe.Server/Components/Pages/CustomerDetail.razor
  - src/Cafe.Server/Components/Pages/Packages.razor
  - src/Cafe.Server/Data/CafeDbContext.cs
  - src/Cafe.Server/Data/Customer.cs
  - src/Cafe.Server/Data/CustomerMinuteLedger.cs
  - src/Cafe.Server/Data/PackageSale.cs
  - src/Cafe.Server/Data/TimePackage.cs
  - src/Cafe.Server/Data/SessionModeExtensions.cs
  - src/Cafe.Server/Data/Migrations/20261120065135_AddCustomersAndPackages.cs
  - src/Cafe.Server/Program.cs
findings:
  critical: 1
  warning: 4
  info: 4
  total: 9
status: issues_found
---

# Phase 5: Code Review Report

**Reviewed:** 2026-11-20
**Depth:** standard
**Files Reviewed:** 31 source files (test files were read only for context)

## Summary

The core design is sound. The ledger is append-only and a bucket balance is a SUM. Every balance mutation (sale, adjust, balance start, extend from balance) takes `BalanceLock`. The sale, payment and ledger credit go in one `SaveChanges`. A balance start or extension commits the debit rows together with the session row, and the `Session.Version` concurrency token makes a racing stop or extend roll back its ledger rows. Phone normalization is digits-only with a unique index, and the soft-delete path has no hard delete. The new pages carry `[Authorize]`. No `MarkupString` appears in any Phase 5 component, and the user-controlled strings (name, notes, phone, ledger note) all render through Razor's default escaping.

The one real integrity defect is in the refund cap, which does not do what its comment says (CR-01). The rest is robustness and UX correctness.

## Critical Issues

### CR-01: Refund after a paid extension of a Balance session credits balance minutes the customer paid cash for

**File:** `src/Cafe.Server/Sessions/SessionService.cs:559-571` (also `BalanceService.cs:219-257`)
**Issue:** `RefundableMinutesAsync` computes `min(purchased - ceil(used/60), netDrawn)`. The doc comment promises that "paid-extension minutes are never refunded", but the cap only bounds the refund by the total drawn from balance. It does not account for the order in which minutes are consumed. The balance-funded minutes are consumed first and the paid-extension minutes last, so any unused minutes are the paid ones.

Worked example:
- A Balance session starts with 60 min drawn from balance.
- Staff use the "pay for the extension instead" fallback, `ExtendAsync` +30, and the cash charge goes into `ChargedAmount`. `PurchasedMinutes` is now 90.
- The customer uses 70 min and stops.
- Unused is 20 min and net drawn is 60, so 20 min are refunded to balance.
- The customer has paid cash for 30 extension min, used only 10 of them, and gets 20 min of prepaid balance back. The cash is never refunded, so unused paid minutes are silently converted into free balance.

The correct refund is `max(0, drawn - used)`, which is 0 here. In general, paid minutes are consumed after balance minutes, so only the part of the balance draw that is still unconsumed is refundable. Balance-funded `ExtendFromBalance` calls after the paid one have the same ordering problem.

**Fix:**
```csharp
// minutes consumed first come out of the balance draw; only what remains of the draw is refundable
var drawn = (await BalanceService.NetDrawnByBucketAsync(db, session.Id, ct)).Sum(b => b.Minutes);
return Math.Max(0, drawn - used);   // NOT min(unused, drawn)
```
For a session with no paid extension, `purchased == drawn`, so `drawn - used == purchased - used`. Existing behavior is preserved and the paid-extension case is fixed. Add a test for the 60 + paid 30, used 70 case. Also decide whether paid extension of a Balance session should be allowed at all, or should be recorded as a separate cash sale.

## Warnings

### WR-01: CustomerDetail can show customer A's data while operating on customer B

**File:** `src/Cafe.Server/Components/Pages/CustomerDetail.razor:233-256`
**Issue:** `OnParametersSetAsync` reloads on every `Id` change, for example by navigating from `/customers/3` to `/customers/7` in the same circuit. In `LoadAsync`, if `GetDetailAsync` throws, the `catch` sets `errorMessage` but leaves `detail` pointing at the previous customer. The page still renders customer 3's name and balance, but "Sell package" and "Adjust balance" now use `Id` (7). A package could be sold, or minutes adjusted, on the wrong customer while staff believe they are acting on the displayed one. `errorMessage` is also never cleared on a later successful load, so the stale error can persist.

**Fix:** Set `detail = null; errorMessage = null;` at the top of `LoadAsync`. For the failure case, also clear the `packages` and `types` lists.

### WR-02: Deleting a station type that is referenced only by packages or ledger rows reports a wrong reason

**File:** `src/Cafe.Server/Stations/StationTypeService.cs:114-135` (the FK `Restrict` is added in `CafeDbContext.cs:147-160`)
**Issue:** Phase 5 adds restrict-FKs from `TimePackages` and `CustomerMinuteLedger` to `StationTypes`. `StationTypeService.DeleteAsync` was not updated to check them. When the delete hits the FK, the catch block finds no stations (`now == 0`), then falls back to `Math.Max(rules, 1)`, producing "used by 1 rate rule(s)". Nothing used it as a rate rule. Staff are told to fix the wrong thing and cannot delete the type. PackageSales hold no FK on the type, so this is the only blocking path.

**Fix:** Count `TimePackages` and `CustomerMinuteLedger` rows for the type and return an accurate message ("used by N package(s) / customer balances"). Do the same in the post-FK-failure branch.

### WR-03: ExtendFromBalance and AdjustAsync ignore the customer's soft-delete state; StartFromBalance revalidation is the only guard

**File:** `src/Cafe.Server/Sessions/SessionService.cs:379-399`
**Issue:** `ExtendFromBalanceAsync` debits the session's customer without re-checking `Customer.IsActive` or existence. Soft-delete says an inactive customer cannot be linked to new work. Deactivating a customer mid-session still lets their balance be spent on extensions. This is probably acceptable, but it is undocumented and untested, so it is a semantic gap. `AdjustAsync` explicitly allows inactive customers, so the same decision should be stated for extensions.

**Fix:** Either call `CustomerService.LoadLinkableAsync` in the extension and fail with the inactive message, or add a comment and test stating that running sessions keep spending.

### WR-04: Package price has no decimal-place validation, and a sale charges whatever the package price is at click time

**File:** `src/Cafe.Server/Customers/TimePackageInput.cs:13-14` and `PackageService.cs:SellAsync`
**Issue:** `[Range(0, 100000000)]` on a decimal accepts `12.345`. That value is stored as TEXT and written to `Payment.Amount` and `PackageSale.Price` unrounded, so revenue totals carry sub-cent fractions. `SellAsync` snapshots the package's current price at the instant of the click. If another operator edits the package price between the picker render and the click, the customer is charged an amount the staff member never saw (`StopAsync` has an `expectedAmount` guard for exactly this case, but sale does not).

**Fix:** Validate `Price == Math.Round(Price, 2)`. Pass the displayed price to `SellAsync` as `expectedPrice` and fail with a "price changed" message if it differs.

## Info

### IN-01: Phone uniqueness is only on raw digits

**File:** `src/Cafe.Server/Customers/CustomerService.cs:200-210`
**Issue:** `+84 90 123 4567` and `090 123 4567` normalize to different digit strings, so the same real number can exist twice and balances can split across duplicate customers. Non-ASCII digits are silently dropped by `char.IsAsciiDigit`. This is a policy choice, but it is not documented.
**Fix:** Document it, or canonicalize a configured country prefix (leading `00` or `+<cc>` to `0`).

### IN-02: Customer search scans every customer on each keystroke with no debounce

**File:** `src/Cafe.Server/Customers/CustomerService.cs:100-125`, `CustomerPicker.razor:84`
**Issue:** The result is capped at 50 (the picker takes 8), but the name filter reads all active customers into memory per `@oninput`, per circuit. This is fine at cafe scale. It will degrade if the customer table grows to tens of thousands of rows. Superseded searches are discarded, not cancelled.
**Fix:** Add `@oninput` debounce (about 200 ms) and optionally cache the id/name/phone projection with short invalidation.

### IN-03: SaveChanges failures in sale and adjust are swallowed without logging

**File:** `src/Cafe.Server/Customers/PackageService.cs:SellAsync` (catch `DbUpdateException`), `BalanceService.cs:122-126`
**Issue:** Both services return "could not be saved" but never log the exception, so a persistent DB fault (locked file, disk full) is invisible to operators. Neither service takes an `ILogger`.
**Fix:** Inject `ILogger<>` and log the exception before returning the failure.

### IN-04: `BalanceLock` is a process-wide singleton semaphore with no timeout

**File:** `src/Cafe.Server/Customers/BalanceLock.cs:11-15`
**Issue:** Every balance mutation across all customers serializes on one gate with an unbounded wait (honors only the caller's token). A hung DB call inside the lock would block every balance operation. Acceptable for under 15 stations, but a per-customer keyed lock or a wait timeout would avoid the global stall.
**Fix:** Optional: key the gate by customer id, or use `WaitAsync(timeout)` and return "busy, try again".

---

_Reviewed: 2026-11-20_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
