---
phase: 06-pos-receipts
reviewed: 2026-11-20T00:00:00Z
depth: standard
files_reviewed: 52
files_reviewed_list:
  - src/Cafe.Server/Payments/AllocationPlanner.cs
  - src/Cafe.Server/Payments/OutstandingCalculator.cs
  - src/Cafe.Server/Payments/SettlementLock.cs
  - src/Cafe.Server/Payments/SettlementService.cs
  - src/Cafe.Server/Payments/TenderCalculator.cs
  - src/Cafe.Server/Payments/CheckoutService.cs
  - src/Cafe.Server/Payments/PaymentModels.cs
  - src/Cafe.Server/Payments/PaymentHistoryService.cs
  - src/Cafe.Server/Payments/PosServiceCollectionExtensions.cs
  - src/Cafe.Server/Orders/OrderService.cs
  - src/Cafe.Server/Orders/OpenTabsService.cs
  - src/Cafe.Server/Orders/Cart.cs
  - src/Cafe.Server/Orders/OrderDraft.cs
  - src/Cafe.Server/Customers/PackageService.cs
  - src/Cafe.Server/Products/ProductService.cs
  - src/Cafe.Server/Products/ProductInput.cs
  - src/Cafe.Server/Data/CafeDbContext.cs
  - src/Cafe.Server/Data/Migrations/20261120071317_AddPos.cs
  - src/Cafe.Server/Data/Order.cs
  - src/Cafe.Server/Data/OrderLine.cs
  - src/Cafe.Server/Data/Payment.cs
  - src/Cafe.Server/Data/PaymentAllocation.cs
  - src/Cafe.Server/Data/PosEnums.cs
  - src/Cafe.Server/Data/Product.cs
  - src/Cafe.Server/Receipts/EscPosEncoder.cs
  - src/Cafe.Server/Receipts/EscPosReceiptRenderer.cs
  - src/Cafe.Server/Receipts/IReceiptPrinter.cs
  - src/Cafe.Server/Receipts/NetworkEscPosPrinter.cs
  - src/Cafe.Server/Receipts/ReceiptBuilder.cs
  - src/Cafe.Server/Receipts/ReceiptModels.cs
  - src/Cafe.Server/Receipts/ReceiptPrintService.cs
  - src/Cafe.Server/Receipts/ReceiptService.cs
  - src/Cafe.Server/Receipts/ReceiptServiceCollectionExtensions.cs
  - src/Cafe.Server/Receipts/ReceiptSettings.cs
  - src/Cafe.Server/Receipts/ReceiptSettingsService.cs
  - src/Cafe.Server/Receipts/ReceiptTextLayout.cs
  - src/Cafe.Server/Receipts/WindowsRawPrinter.cs
  - src/Cafe.Server/Program.cs
  - src/Cafe.Server/Components/Pages/Pos.razor
  - src/Cafe.Server/Components/Pages/Receipt.razor
  - src/Cafe.Server/Components/Pages/CustomerDetail.razor
  - src/Cafe.Server/Components/Pages/Settings.razor
  - src/Cafe.Server/Components/Layout/ReceiptLayout.razor
  - src/Cafe.Server/Components/Shared/PaymentDialog.razor
  - src/Cafe.Server/Components/Shared/PaymentDialogResult.cs
  - src/Cafe.Server/Components/Shared/OpenTabsPanel.razor
  - src/Cafe.Server/Components/Shared/PrintReceiptButton.razor
  - src/Cafe.Server/Components/Shared/ReceiptSettingsPanel.razor
  - src/Cafe.Server/Components/Shared/StationCard.razor
  - src/Cafe.Server/Sessions/SessionService.cs
  - src/Cafe.Server/Pricing/PricingEngine.cs
findings:
  critical: 0
  warning: 6
  info: 5
  total: 11
status: issues_found
---

# Phase 6: Code Review Report

**Reviewed:** 2026-11-20
**Depth:** standard
**Files Reviewed:** 52 (tests and Products.razor/NavMenu only skimmed)
**Status:** issues_found

## Summary

The core money path is sound. The allocation planner waterfall, the outstanding calculation (never negative, summed in memory), the expected-due guard inside `SettlementLock`, the single-`SaveChanges` settlement and the package-sale transaction all hold up under tracing. Voiding is correctly refused once any allocation exists. The migration moves Phase 5 payments into allocations correctly and does not double-count a payment that has both a PackageSaleId and a SessionId. No XSS: there is no `MarkupString` anywhere and all user text goes through Razor encoding. Receipt, POS and Settings pages are `[Authorize]` (also via `_Imports`). The Windows P/Invoke signatures are correct and the print handle is released on every path. The network printer links its timeout token and disposes its socket.

No blockers found. The warnings below concern stale-quote UX that will bite in daily use, free-package handling, reprint accuracy and print output for Vietnamese text.

## Warnings

### WR-01: Stop & settle on an open-ended session goes stale within about a minute

**File:** `src/Cafe.Server/Components/Shared/StationCard.razor:555-580`, `src/Cafe.Server/Payments/CheckoutService.cs:48-56`, `src/Cafe.Server/Pricing/PricingEngine.cs:40-44`
**Issue:** The open-ended charge is priced per started minute. The dialog freezes `_confirmQuote` / `_confirmTotalDue` at quote time. `ConfirmSettleAsync` first compares the fresh `TotalDue`, and `SessionService.StopAsync` then compares `expectedAmount`. Both fail with "Amounts changed" as soon as the clock crosses a minute boundary. Staff typing tender amounts in the payment dialog will frequently exceed that window. They then have to close the dialog and repeat it, which can loop. The guard is correct for protecting the customer, but the flow is not usable for open-ended sessions.
**Fix:** Either let the dialog refresh the quote and re-show the new due, with an explicit "Charge changed to X, continue?" step instead of aborting. Or accept a tolerance by snapshotting the quote time and having `StopAsync` price at that instant. At minimum, auto-requote and reopen the dialog on `ChangedMessage` instead of dropping back to the card.

### WR-02: `CheckoutService.StopAndSettleAsync` does not guard the open-order total before stopping

**File:** `src/Cafe.Server/Payments/CheckoutService.cs:48-90`
**Issue:** `StopAsync` is protected by version and charge only. An order added, voided or paid between quote and stop does not bump the session version. `StopAsync` then succeeds and the session is irreversibly ended. `SettleAsync` then fails with `ChangedMessage`, returned as `PaymentError`. The UI mitigates this with a pre-check in `ConfirmSettleAsync`, but that check is a separate non-atomic read, and the service contract itself ("stale quote guard") is incomplete. Other callers get no protection. The failure leaves the session stopped and unpaid, which is the "failure after stop" case the plan wanted to avoid.
**Fix:** Inside `StopAndSettleAsync`, call `OwingOrdersAsync` before `sessions.StopAsync` and compare `charge + owing.Sum(Outstanding)` to `expectedDue`. Return `ChangedMessage` without stopping on a mismatch. Keep the post-stop `SettleAsync` check as a backstop.

### WR-03: Free ($0) package cannot be sold through the UI, and a free sale writes no allocation

**File:** `src/Cafe.Server/Components/Shared/PaymentDialog.razor:150` (`CanConfirm` requires `Applied > 0`), `src/Cafe.Server/Customers/PackageService.cs:182-188, 244-251`
**Issue:** `SellCoreAsync` explicitly supports `Price == 0`, but `CustomerDetail` always goes through `PaymentDialog`, where `Record payment` stays disabled when `Applied == 0`. A $0 package therefore cannot be sold from the UI. Via `SellAsync` a free sale creates a zero Payment with no allocation (`AllocationPlanner` yields no parts). The migration, by contrast, creates a 0-amount allocation for free Phase 5 sales. The new `ReceiptService` finds no targets for such a payment and renders a blank receipt, and `PaymentHistoryService.ForCustomerAsync` omits it.
**Fix:** In `CustomerDetail.BeginSell`, bypass the dialog for `Price == 0` and call `SellPaidAsync` directly. In `SellCoreAsync`, add a zero-amount allocation for free sales so receipts and history behave consistently with migrated data.

### WR-04: Receipt "Remaining" is computed at reprint time, so old receipts change

**File:** `src/Cafe.Server/Receipts/ReceiptService.cs:44-52, 83-95`
**Issue:** `Remaining` for each target uses all allocations that exist now (`allocatedAll`). It does not use allocations up to this checkout. A partial payment receipt reprinted after the balance was later settled shows Total 100, Paid 40, no "Remaining" line. The figures do not reconcile, and the printed receipt disagrees with the one handed to the customer. The same applies to the on-screen "Still owing".
**Fix:** Compute remaining as of the receipt: sum only allocations whose `PaymentId <= maxPaymentIdInThisReceipt` (ids are monotonic), or allocations with `AtUtc <= receipt time`.

### WR-05: Vietnamese (and any non-CP437) text prints as '?' on thermal receipts

**File:** `src/Cafe.Server/Receipts/EscPosEncoder.cs:21-31, 87-103`
**Issue:** Text is encoded to CP437 with a `?` replacement fallback, with no diacritic folding. `Cafe:Culture` is documented with `vi-VN` as the example, so product, cafe and customer names containing characters like `ạ ư ơ ả đ` will print as '?'. A decomposed (NFD) string gives one '?' per combining mark. Product names are the main receipt content.
**Fix:** In `Normalise`, apply `NormalizationForm.FormD`, drop `NonSpacingMark` runes, and map `đ/Đ` to `d/D` (and similar) before the CP437 fallback. Alternatively, send a code-page select (`ESC t n`) with the matching encoding. Add a test with "Cà phê sữa đá".

### WR-06: Tender amount precision is not validated server-side, and dialog rounding differs from pricing

**File:** `src/Cafe.Server/Payments/SettlementService.cs:150-178`, `src/Cafe.Server/Components/Shared/PaymentDialog.razor:123` (`decimal.Round(v, 2)`), `src/Cafe.Server/Pricing/PricingEngine.cs:67`
**Issue:** `ValidateTenders` accepts any scale (e.g. 10.005, 0.0001). `OrderService`/`ProductInput` also allow unbounded-scale prices. Such values are stored as TEXT decimals and produce sub-cent outstanding amounts that never clear, since the dialog can only enter 2 decimals. The dialog also rounds with banker's rounding (`MidpointRounding.ToEven`) while the pricing engine uses `AwayFromZero`, so a typed 0.125 becomes 0.12.
**Fix:** In `ValidateTenders` reject amounts where `decimal.Round(a, 2) != a`. Constrain product and package prices to 2 decimals. Use `MidpointRounding.AwayFromZero` in the dialog.

## Info

### IN-01: Denormalised `Payment.SessionId` / `OrderId` / `PackageSaleId` set on payments that paid none of that target

**File:** `src/Cafe.Server/Payments/SettlementService.cs:99-101`
**Issue:** `SoleId` is applied to every payment of the checkout. If payment 1 fully covers the session and payment 2 only the order, both carry both ids. Anything reading these columns instead of allocations (CustomerDetail's package method lookup is the exception, and is correct) will misattribute amounts.
**Fix:** Set the id only on payments whose plan parts include that target, or document and enforce that readers use allocations only.

### IN-02: "Charge now" with Session/Customer attach selected but nothing chosen silently creates a walk-in order

**File:** `src/Cafe.Server/Components/Pages/Pos.razor:236-247, 297-340`
**Issue:** `EffectiveSessionId`/`EffectiveCustomerId` are null when the picker is empty, so `ChargeNowAsync` proceeds as a walk-in. `Add to tab` is correctly disabled; `Charge now` is not.
**Fix:** Disable `Charge now` or show an error when an attach mode is selected without a target.

### IN-03: Misleading double-height comment and wrap width

**File:** `src/Cafe.Server/Receipts/EscPosEncoder.cs:35`, `src/Cafe.Server/Receipts/EscPosReceiptRenderer.cs:30-31`
**Issue:** `GS ! 0x01` doubles height only, not width, so wrapping the cafe name at `columns / 2` wastes space and the comment is inaccurate. Use `0x11` for double width and height, which does justify the half-width wrap, or wrap at full width with `0x01`.
**Fix:** Decide which is intended and make the byte, wrap width and comment agree.

### IN-04: Network printer host/port is an unrestricted outbound connection target

**File:** `src/Cafe.Server/Receipts/NetworkEscPosPrinter.cs:20-27`, `src/Cafe.Server/Receipts/ReceiptSettingsService.cs:76-79`
**Issue:** Any authenticated admin can make the server open a TCP connection to any host:port and send ESC/POS bytes (the test-print button triggers it on demand). Acceptable for a single trusted admin on a LAN, but worth noting if more accounts or roles are ever added.
**Fix:** Optionally restrict to private-range addresses; document the trust assumption.

### IN-05: Receipt amounts use the thread culture's `CurrencyDecimalDigits`

**File:** `src/Cafe.Server/Receipts/ReceiptTextLayout.cs:10-11`
**Issue:** `FormatAmount` uses `"N" + CurrencyDecimalDigits` under the current culture, so printed amounts silently drop fractional parts under vi-VN (0 digits) while the stored amounts keep 2 decimals. Together with WR-06 this makes a receipt total potentially differ from the sum of its printed lines by rounding.
**Fix:** Keep all amounts whole in the currency's minor unit (validate on entry), or format with explicit digits and rely on WR-06's validation.

---

_Reviewed: 2026-11-20_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
