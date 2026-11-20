---
phase: 06-pos-receipts
plan: 05
subsystem: pos-ui
tags: [blazor, stop-settle, package-sale, receipts, settings]
requires:
  - phase: 06-pos-receipts
    provides: CheckoutService, PaymentDialog, PrintReceiptButton, ReceiptSettingsService, ReceiptPrintService, PaymentHistoryService
provides:
  - Stop & settle / Stop, pay later on the station card
  - Package sale through PaymentDialog, customer Receipts card with Reprint
  - ReceiptSettingsPanel with Print test receipt, receipt page auto-print (?print=1)
affects: [phase-7-reports]
key-files:
  created:
    - src/Cafe.Server/Components/Shared/ReceiptSettingsPanel.razor
    - tests/Cafe.Tests/StopSettleFlowTests.cs
    - tests/Cafe.Tests/PosReceiptPagesTests.cs
  modified:
    - src/Cafe.Server/Components/Shared/StationCard.razor
    - src/Cafe.Server/Components/Pages/CustomerDetail.razor
    - src/Cafe.Server/Components/Pages/Settings.razor
    - src/Cafe.Server/Components/Pages/Receipt.razor
    - src/Cafe.Server/Payments/CheckoutService.cs
    - tests/Cafe.Tests/CustomerPagesTests.cs
key-decisions:
  - "The stale-total guard lives on the card (re-quote before StopAndSettleAsync), not in CheckoutService: Plan 03's contract (stop first, payment refusal returned as PaymentError) is kept and its test untouched."
  - "An open payment dialog survives the session ending (the card does not clear confirm state while the dialog is open); staff close it."
status: complete
commits: 4
plan_head_before: 629db2608b131c3c7d05372cdb6be6849d443d1e
plan_head_after: 16fd957260dd9843fb600d7a648ce248f5744a14
actuals:
  tasks: 3
  commits: 4
metrics:
  completed: 2026-11-20
---

# Phase 6 Plan 05: Stop & settle, package dialog, receipt settings Summary

Every payment path now goes through the single PaymentDialog: stopping a session offers Stop & settle or Stop, pay later, packages are sold through the dialog, receipts can be reprinted from the customer page, and receipt printing is configurable and testable from Settings.

## Tasks

| Task | Commit | Notes |
| ---- | ------ | ----- |
| 1 Stop & settle / pay later | b21be33 | StationCard quote shows charge, open orders, total due; refund line and version binding kept |
| 2 Package sale + customer receipts | 3337938 | SellPaidAsync with RequireFullPayment; Receipts card with Reprint |
| 3 Receipt settings + auto-print | e0702d3 | ReceiptSettingsPanel, Print test receipt, `?print=1` calls window.print once |
| Fix | 16fd957 | Card re-quotes before stopping; CheckoutService contract restored |

## Deviations from Plan

**1. [Rule 1 - Bug] Stale-quote handling moved to the card.** I first added a pre-stop total check inside `CheckoutService.StopAndSettleAsync`; it broke Plan 03's `D05_payment_failure_after_a_successful_stop...` test, which defines the service contract (stop first, refusal becomes `PaymentError`). Reverted the service change and put the check in `StationCard.ConfirmSettleAsync`. The service-level stale test therefore asserts `PaymentError = ChangedMessage`, no payment, all items listed under Open tabs. Commit 16fd957.

**2. Test file naming.** Plan listed `PosReceiptPagesTests`; it is a new file (existing `PosPagesTests` untouched). No Phase 5 test was deleted; `CustomerPagesTests` only gained a "Receipts" assertion because it never asserted the old Cash/Card selector.

## Human-only checks (not run; user asleep)

- With a thermal printer or `nc -l 9100`, Print test receipt produces output; an unreachable host shows a red error without breaking the page.
- In a real browser, finishing a payment then Print receipt opens a new tab that prints a narrow receipt; `/receipts/{id}?print=1` opens the print dialog once.
- Visual check of the stop-confirm panel and payment dialog on the dashboard.

## Known Stubs

None.

## Threat Flags

None. Threat mitigations T-06-19 (pay-later leaves items in Open tabs, PaymentError surfaced, tested) and T-06-20 (version and amounts bound, card re-quote) applied; pages stay behind [Authorize].

## Verification

`dotnet build Cafe.sln` clean; `dotnet test Cafe.sln`: 920 passed, 0 failed.

## Self-Check: PASSED
