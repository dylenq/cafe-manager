---
phase: 06-pos-receipts
plan: 04
subsystem: pos-ui
tags: [blazor, pos, payment-dialog, open-tabs, products]
requires:
  - phase: 06-pos-receipts
    provides: plans 02 and 03 services (ProductService, OrderService, OpenTabsService, SettlementService, TenderCalculator, PaymentHistoryService, ReceiptPrintService)
provides:
  - Cart (pure model), /products, /pos, PaymentDialog (+PaymentDialogResult), PrintReceiptButton, OpenTabsPanel, nav links
affects: [06-05]
key-files:
  created:
    - src/Cafe.Server/Orders/Cart.cs
    - src/Cafe.Server/Components/Pages/Products.razor
    - src/Cafe.Server/Components/Pages/Pos.razor
    - src/Cafe.Server/Components/Pages/Pos.razor.css
    - src/Cafe.Server/Components/Shared/PaymentDialog.razor
    - src/Cafe.Server/Components/Shared/PaymentDialog.razor.css
    - src/Cafe.Server/Components/Shared/PaymentDialogResult.cs
    - src/Cafe.Server/Components/Shared/PrintReceiptButton.razor
    - src/Cafe.Server/Components/Shared/OpenTabsPanel.razor
    - tests/Cafe.Tests/CartTests.cs
    - tests/Cafe.Tests/PosPagesTests.cs
  modified:
    - src/Cafe.Server/Components/Layout/NavMenu.razor
key-decisions:
  - "PaymentDialogResult lives in its own Components/Shared/PaymentDialogResult.cs: a record declared inside a Razor @code block becomes a nested type that other components cannot reference by its plain name."
  - "A cart line at the 99 cap makes Add return false; the POS ignores the tap silently."
  - "Open tabs recognises a running-session group by the ' - open session' header suffix plus SessionId, because OpenTabGroup does not expose session status."
  - "Charge now with Active session or Customer mode but nothing chosen is treated as a walk-in order; only Add to tab requires a choice."
status: complete
completed: 2026-11-20
actuals:
  tasks: 3
  commits: 3
plan_head_before: 419acd6f7b97e39784f8b4a9e3e350feda11b461
plan_head_after: 2854dc0ddff7553a48ff28feda6dce1e1654789b
commits: 3
---

# Phase 6 Plan 04: POS UI Summary

Staff can now manage products, ring up orders on touch tiles, attach them to a session, customer or walk-in, charge with split/change/partial tenders, add to tabs, settle or void open tabs, and reprint receipts, all through one reusable payment dialog.

## Tasks

| Task | Name | Commit |
| ---- | ---- | ------ |
| 1 | Cart model, /products page and navigation | 1ac1326 |
| 2 | PaymentDialog and PrintReceiptButton | 20dbfd3 |
| 3 | /pos page, Open tabs panel, recent receipts, smoke tests | 2854dc0 |

## What was built
- **Cart**: pure class with accumulate, increment/decrement (decrement at 1 removes), remove, clear, cap 99, decimal-exact total, `ToDraft`.
- **/products**: category-grouped tables, add/edit form with category datalist, Activate/Deactivate, Delete behind an inline Yes/No, service error (deactivate hint) shown, Show inactive toggle.
- **PaymentDialog**: inline modal; items with Total/Paid/Outstanding, several Cash/Card rows, Exact cash and Rest by card, live Applied/Change due/Remaining via TenderCalculator, `RequireFullPayment`, busy flag against double submit, changed-amounts message with a reopen hint, success view with change, remaining, receipt number and Print receipt.
- **PrintReceiptButton**: BrowserUrl opens a new tab via `window.open`; Printed and Skipped show inline text; Failed or exceptions show a dismissible alert and never touch the payment.
- **/pos**: category tabs, tiles (min 96px, palette picked by a stable category hash), cart with +/-, attach selector (Walk-in / Active session / Customer via CustomerPicker), optional note, Charge now / Add to tab / Clear. Cancelling the dialog voids the just-created order; zero-total orders skip the dialog.
- **OpenTabsPanel**: groups with Settle (ExpectedDue = group outstanding) and inline-confirmed Void per order; exposes `RefreshAsync` and `OnChanged`. Recent receipts card with Reprint.
- NavMenu: POS and Products links (additive). No Program.cs, Payments, Receipts or schema change, no packages.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] PaymentDialogResult placement**
- **Found during:** Task 2
- **Issue:** the plan says to declare the record at the bottom of the component's @code, but that makes it a nested type of the component, unusable by Pos and OpenTabsPanel by its plain name.
- **Fix:** declared it as a top-level public record in `Components/Shared/PaymentDialogResult.cs`, same namespace as planned; Payments/PaymentModels.cs untouched.
- **Commit:** 20dbfd3

Otherwise the plan was executed as written.

## Human checks pending
- On a tablet-width browser, confirm tiles are at least 96px tall and tappable without zooming.
- Click through Charge now (cash with change, split cash+card, partial), cancel the dialog (no orphan order left), Add to tab, Settle and Void in the panel, and Print/Reprint with the real printer mode. The interactive flows have no browser harness; only server-rendered output is covered by tests.
- Check the dialog scrolls on a small screen.

## Known Stubs
None.

## Threat Flags
None beyond the plan's threat model (T-06-16 ExpectedDue sent and service-checked; T-06-17 busy flags on dialog, Charge, Add to tab and Void; T-06-18 cancelled charge voids its order).

## Verification
- `dotnet build Cafe.sln`: 0 warnings, 0 errors.
- `dotnet test Cafe.sln`: 907 passed, 0 failed.
- REQUIREMENTS.md not updated: POS-01..03 stay open until plan 05 completes the phase.

## Self-Check: PASSED
