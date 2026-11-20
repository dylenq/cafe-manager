---
phase: 06-pos-receipts
fixed_at: 2026-11-20T00:00:00Z
review_path: /Users/dylenquyn/Cafe/.planning/phases/06-pos-receipts/06-REVIEW.md
iteration: 1
findings_in_scope: 9
fixed: 9
skipped: 0
status: all_fixed
---

# Phase 6: Code Review Fix Report

**Fixed at:** 2026-11-20
**Source review:** /Users/dylenquyn/Cafe/.planning/phases/06-pos-receipts/06-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 9 (WR-01..WR-06, IN-01, IN-02, IN-05; IN-03 and IN-04 out of scope by request)
- Fixed: 9
- Skipped: 0

**Verification ran in:** the main checkout on branch `main` (no worktree: plan 07-03 runs concurrently in its own worktree, and the fixer worked directly on `main` as instructed). `dotnet build Cafe.sln` is clean (0 warnings, 0 errors) and `dotnet test Cafe.sln` passes (1026 passed, 0 failed) after the last fix.

## Fixed Issues

### WR-02: StopAndSettleAsync did not guard the open-order total before stopping

**Files modified:** `src/Cafe.Server/Payments/CheckoutService.cs`, `src/Cafe.Server/Payments/SettlementService.cs`, `tests/Cafe.Tests/SettlementServiceTests.cs`, `tests/Cafe.Tests/StopSettleFlowTests.cs`
**Commit:** 27d12fc
**Applied fix:** `StopAndSettleAsync` now takes `SettlementLock`, re-quotes (session charge plus open orders) under it, and returns `SettlementService.ChangedMessage` without stopping when the total differs from `expectedDue`. Tenders are validated up front so an invalid tender no longer stops the session first. `SettlementService.SettleAsync` was split into a locking wrapper and an internal `SettleLockedAsync` so stop and settle share one lock hold (the semaphore is not re-entrant); the post-stop expected-due check remains as a backstop.
**Tests:** the stale-extra-order test now expects "nothing stopped, nothing paid, changed message". The D05 "payment failure after stop" test now triggers the failure with an overpaying tender, since a wrong `expectedDue` is refused before the stop.

### WR-01: Stop & settle on an open-ended session went stale within a minute

**Files modified:** `src/Cafe.Server/Payments/CheckoutService.cs`, `src/Cafe.Server/Components/Shared/StationCard.razor`, `tests/Cafe.Tests/StopSettleFlowTests.cs`
**Commit:** 373de4b
**Applied fix:** Added the pure `CheckoutService.OnlyElapsedChargeChanged(shown, current, openEnded)`: true only for an open-ended session whose version and open orders are unchanged but whose session charge differs. In `StationCard.ConfirmSettleAsync`, when the pre-check or the service reports a change and that rule holds, the card updates its quote and total (the dialog re-renders with the new Due) and returns "The session charge rose from X to Y as time passed. Check the payment, then press Record payment to confirm the new amount." The dialog stays open and staff confirm once more. Prepaid and balance sessions, changed orders and version bumps keep the strict path (close and re-quote). Because the service now guards the full due before stopping (WR-02), a minute boundary crossed between the re-read and the stop re-quotes instead of leaving a stopped, unpaid session.
**Status note:** requires human verification. The re-confirm UI path is Blazor server interaction with no bUnit harness here, so only the decision rule and the service behaviour are unit-tested; staff should try it on a live open-ended session.

### WR-03: Free ($0) package could not be sold through the UI and wrote no allocation

**Files modified:** `src/Cafe.Server/Customers/PackageService.cs`, `src/Cafe.Server/Components/Shared/PaymentDialog.razor`, `src/Cafe.Server/Components/Pages/CustomerDetail.razor`, `tests/Cafe.Tests/PackageServiceTests.cs`
**Commit:** 3ef74b2
**Applied fix:** Decision: allow free sales in the dialog (rather than bypassing it). `PaymentDialog` has a new `AllowZeroDue` parameter (set only by CustomerDetail's package sale). When Due is 0 it shows "Nothing to pay. Record the free sale.", hides the tender rows and enables Record payment with no tender lines. `SellCoreAsync` writes a zero-amount allocation for a free sale, matching migrated Phase 5 free sales, so receipts and customer history show it. Tested: zero allocation, one history entry, and a non-blank receipt.

### WR-04: Receipt "Remaining" changed on reprint

**Files modified:** `src/Cafe.Server/Receipts/ReceiptService.cs`, `tests/Cafe.Tests/ReceiptServiceTests.cs`
**Commit:** 1e30570
**Applied fix:** Only allocations with `PaymentId <= last payment id of this checkout` count towards Remaining. Decision: payment ids instead of `AtUtc`, because ids only grow and never tie (several payments can share one timestamp, including a frozen test clock). The old reprint test asserted the changed figure; it now expects the original Remaining (30000), and the later settlement's own receipt shows 0.

### WR-05: Vietnamese and other non-CP437 text printed as '?'

**Files modified:** `src/Cafe.Server/Receipts/EscPosEncoder.cs`, `tests/Cafe.Tests/EscPosEncoderTests.cs`, `tests/Cafe.Tests/ReceiptTextLayoutTests.cs`
**Commit:** 2f601c1
**Applied fix:** Text is composed (FormC) first, then each character CP437 can print is kept as is. Only characters CP437 cannot print are folded (FormD, non-spacing marks dropped, `đ/Đ` to `d/D`); anything still unmappable stays '?'. Decision: keep CP437-printable accents (é, à, ê, á) rather than flatten everything to ASCII, which preserves the existing "Café" behaviour and prints more faithfully. "Cà phê sữa đá" prints as "Cà phê sua dá"; decomposed input gives the same result. Two existing tests used "ễ" as their "unprintable" sample; they now use "漢" (and ToPrintable("ễ") is "e").

### WR-06: Tender and price precision not validated, dialog rounding differed from pricing

**Files modified:** `src/Cafe.Server/Payments/SettlementService.cs`, `src/Cafe.Server/Payments/TenderCalculator.cs`, `src/Cafe.Server/Components/Shared/PaymentDialog.razor`, `src/Cafe.Server/Products/ProductInput.cs`, `tests/Cafe.Tests/SettlementServiceTests.cs`, `tests/Cafe.Tests/TenderCalculatorTests.cs`, `tests/Cafe.Tests/ProductServiceTests.cs`
**Commit:** 4eb5279
**Applied fix:** `ValidateTenders` rejects amounts or tendered values with more than 2 decimals ("Payment amounts can have at most 2 decimal places."), which also covers package sales. `ProductInput` now validates price to 2 decimals like `TimePackageInput` already did (package prices needed no change). The dialog row parser and `TenderCalculator` round with `MidpointRounding.AwayFromZero`, matching `PricingEngine` (0.125 becomes 0.13).

### IN-01: Denormalised Payment.SessionId / OrderId / PackageSaleId set on payments that paid none of that target

**Files modified:** `src/Cafe.Server/Payments/SettlementService.cs`, `tests/Cafe.Tests/SettlementServiceTests.cs`
**Commit:** b4c04c6
**Applied fix:** Decision: kept the columns (minimal risk, existing tests stay meaningful) but set each only when the checkout has exactly one target of that kind and this payment's plan actually allocates a positive amount to it. The new test splits a checkout so payment 1 covers only the session and payment 2 only the order; each carries only its own id.

### IN-02: "Charge now" with an attach mode but no target silently created a walk-in

**Files modified:** `src/Cafe.Server/Orders/AttachRules.cs` (new), `src/Cafe.Server/Components/Pages/Pos.razor`, `tests/Cafe.Tests/AttachRulesTests.cs` (new)
**Commit:** c507c7c
**Applied fix:** New small `AttachKind` enum and `AttachRules.ChargeError`, which replace the page's private enum. `ChargeNowAsync` shows "Choose a session, or switch to Walk-in." (or the customer equivalent) instead of creating a walk-in order. The new files are needed so the rule is unit-testable without a Blazor harness.

### IN-05: Receipt amounts used the thread culture's currency digits

**Files modified:** `src/Cafe.Server/Receipts/ReceiptTextLayout.cs`, `tests/Cafe.Tests/ReceiptTextLayoutTests.cs`
**Commit:** 3aa6bed
**Applied fix:** `FormatAmount` uses `"N2"` with the current culture, so decimals are always 2 and only the separators follow the culture. Tested with a culture whose `CurrencyDecimalDigits` is 0.

## Skipped Issues

None. IN-03 and IN-04 were out of scope for this run.

## Notes for follow-up

- Tests and behaviours changed deliberately: stale-quote and payment-failure tests (WR-02), reprint Remaining (WR-04), and the "ễ" samples (WR-05). Details are above.
- The StationCard re-confirm flow (WR-01) and the free-sale dialog (WR-03) are Blazor UI paths without a component test harness; they are worth one manual check.
- Files owned by plan 07-03 (Program.cs hosting, appsettings.json, CafeDataPaths, logging, BackupService, deploy scripts, test factory backup settings) were not touched. No migrations were added. REVIEW-FIX.md is intentionally uncommitted.

---

_Fixed: 2026-11-20_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
