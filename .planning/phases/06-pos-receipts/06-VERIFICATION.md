---
phase: 06-pos-receipts
verified: 2026-11-20T00:00:00Z
status: human_needed
score: 3/3 must-haves verified
covered_files:
  - src/Cafe.Server/Payments/SettlementService.cs
  - src/Cafe.Server/Receipts/ReceiptPrintService.cs
covered_digest: "v2:sha256:6fe748b8f2d0d69df7cdec83ead26e021ad49b4b2c04a4efbefff8367844d369"
behavior_unverified: 0
human_verification:
  - test: "Print a receipt (Settings > Print test receipt, and after a real payment) to a network ESC/POS thermal printer on port 9100"
    expected: "Receipt prints with correct width, bold/double-height header, partial cut; Vietnamese names print without '?'"
    why_human: "Needs a real thermal printer and its code page support"
  - test: "Switch off or unplug the network printer with a Windows printer name configured, then print"
    expected: "Receipt prints on the Windows printer via winspool RAW; if both fail, the combined error shows and the payment stays saved"
    why_human: "WindowsRawPrinter P/Invoke only runs on Windows with a real queue; not testable on the macOS dev machine"
  - test: "Browser mode: pay on a tablet and open /receipts/{id}?print=1"
    expected: "Narrow monospace layout, print dialog opens, @media print hides chrome"
    why_human: "Needs a real browser print dialog"
  - test: "Touch use of /pos on a tablet (category tabs, cart, attach, Charge now, Add to tab, Stop and settle)"
    expected: "Tap targets usable, totals match the dialog"
    why_human: "Visual and touch UX"
---

# Phase 6: POS & Receipts Verification Report

**Phase Goal:** Staff can sell food and drinks, record payments for sessions, orders and packages, and print receipts
**Verified:** 2026-11-20
**Status:** human_needed
**Re-verification:** No, initial verification
**Evidence base:** code and tests read; the full `dotnet test Cafe.sln` run (1054/1054 passed) was supplied by the caller, no build was run here.

## Observable Truths

| # | Truth (ROADMAP SC) | Status | Evidence |
|---|---|---|---|
| 1 | Manage products; ring up an order attached to a session, customer or walk-in | VERIFIED | `Products.razor` + `ProductService`; `Pos.razor` + `OrderService` (Open/Paid/Voided, add to tab, void), `CheckoutService`; `OpenTabsPanel.razor`. Pages are behind global `[Authorize]`. Tests: `ProductServiceTests`, `OrderServiceTests`, `PosPagesTests`, `PosTracerTests`. |
| 2 | Record cash/card payment for session charges, orders and packages | VERIFIED | `SettlementService` (tender validation to 2 decimals, `AllocationPlanner`, `PaymentAllocation` across session, orders, package sale; overpay and changed-due guards), shared `PaymentDialog.razor` used by `Pos.razor`, `StationCard.razor` (Stop & settle / Stop, pay later) and `CustomerDetail.razor`; `PackageService.SellPaidAsync` writes sale, payments, allocations and ledger atomically. Tests: `SettlementServiceTests`, `StopSettleFlowTests`, `PackagePaymentTests`, `PosMigrationTests`. |
| 3 | Print receipt on network ESC/POS printer, falling back to a Windows printer when unavailable | VERIFIED (code) | `ReceiptPrintService.SendAsync`: Network mode tries `NetworkEscPosPrinter`; on failure and a configured `WindowsPrinter` name, retries `WindowsRawPrinter` (winspool RAW, `OperatingSystem.IsWindows()` guard); combined error otherwise. Own `EscPosEncoder` (CP437 with diacritic folding), `ReceiptBuilder`, `EscPosReceiptRenderer`, Browser mode `Receipt.razor`. Printing runs after payment and never fails it. Tests: `EscPosEncoderTests`, `NetworkEscPosPrinterTests` (in-process TcpListener), `ReceiptBuilderTests`, `ReceiptPrintServiceTests`, `PosReceiptPagesTests`. Real hardware path is human-verified. |

**Score:** 3/3 by code and tests; physical printing needs hardware.

## 06-REVIEW-FIX.md fixes (9/9 present in git history and code)

| Finding | Evidence |
|---|---|
| WR-02 (27d12fc) | `StopAndSettleAsync` guards the open-order due before stopping (commit present; `StopSettleFlowTests`) |
| WR-01 (373de4b) | Open-ended Stop & settle re-quotes and confirms the grown charge once |
| WR-03 (3ef74b2) | Free package: `PackageService.SellCoreAsync` zero tender and zero allocation (`package.Price == 0m` branches) |
| WR-04 (1e30570) | `ReceiptService` "Remaining" computed as of the receipt (line 38 comment, `RemainingOf(..., allocatedAll)`) |
| WR-05 (2f601c1) | `EscPosEncoder` folds đ/Đ and strips NonSpacingMark after FormD before CP437 |
| WR-06 (4eb5279) | `SettlementService` line 171 rejects more than 2 decimals; `TenderCalculator` rounds `AwayFromZero` |
| IN-01 (b4c04c6) | Payment target ids set only for allocated targets (commit present) |
| IN-02 (c507c7c) | "Charge now" refused when an attach mode has no target |
| IN-05 (3aa6bed) | Receipt amounts culture-invariant (commit present) |

## Requirements Coverage

| Req | Status | Evidence |
|---|---|---|
| POS-01 | SATISFIED | Truth 1 |
| POS-02 | SATISFIED | Truth 1 |
| POS-03 | SATISFIED | Truth 2 |
| POS-04 | SATISFIED (code); hardware check pending | Truth 3 |

No orphaned requirements.

## Anti-Patterns

No TBD/FIXME/XXX in Phase 6 files; no stubs found.

## Notes

- CONTEXT described printer modes as a settings choice; the ROADMAP SC fallback (network to Windows) is implemented inside Network mode and is stricter than CONTEXT, so the roadmap contract holds.

## Human Verification Required

See frontmatter: real thermal printer, Windows fallback, browser print, tablet touch UX.

---
_Verified: 2026-11-20_
_Verifier: Claude (gsd-verifier)_
