---
phase: 06-pos-receipts
plan: 03
subsystem: receipts-printing
tags: [escpos, thermal-printer, tcp, winspool, settings]
requires:
  - phase: 06-pos-receipts
    provides: ReceiptBuilder, ReceiptService, Payment allocations (plan 01)
provides:
  - EscPosEncoder (own, CP437), ReceiptTextLayout (42/32 columns, FormatAmount), EscPosReceiptRenderer
  - ReceiptSettings/ReceiptMode/ReceiptSettingsInput/ReceiptSettingsService (keys Receipts:*)
  - IReceiptPrinter, INetworkReceiptPrinter, IWindowsReceiptPrinter, PrintResult
  - NetworkEscPosPrinter, WindowsRawPrinter, ReceiptPrintService (PrintAsync, PrintTestAsync, PrintOutcome)
affects: [06-04, 06-05]
tech-stack:
  added: []
  patterns: [never-throw printing outside the payment transaction, printer slots as interfaces for test swapping]
key-files:
  created:
    - src/Cafe.Server/Receipts/EscPosEncoder.cs
    - src/Cafe.Server/Receipts/ReceiptTextLayout.cs
    - src/Cafe.Server/Receipts/EscPosReceiptRenderer.cs
    - src/Cafe.Server/Receipts/ReceiptSettings.cs
    - src/Cafe.Server/Receipts/ReceiptSettingsService.cs
    - src/Cafe.Server/Receipts/IReceiptPrinter.cs
    - src/Cafe.Server/Receipts/NetworkEscPosPrinter.cs
    - src/Cafe.Server/Receipts/WindowsRawPrinter.cs
    - src/Cafe.Server/Receipts/ReceiptPrintService.cs
    - tests/Cafe.Tests/ (EscPosEncoderTests, ReceiptTextLayoutTests, ReceiptBuilderTests, ReceiptServiceTests, NetworkEscPosPrinterTests, ReceiptPrintServiceTests)
  modified:
    - src/Cafe.Server/Receipts/ReceiptModels.cs
    - src/Cafe.Server/Receipts/ReceiptBuilder.cs
    - src/Cafe.Server/Receipts/ReceiptService.cs
    - src/Cafe.Server/Receipts/ReceiptServiceCollectionExtensions.cs
key-decisions:
  - "ReceiptDocument gained a trailing RootPaymentId (default 0) so the browser URL uses the root id even for a sibling payment id."
  - "Session receipt detail is now 'N min, Mode' with seconds rounded up to whole minutes (was H:MM in plan 01)."
  - "Printer slots are INetworkReceiptPrinter / IWindowsReceiptPrinter so tests swap them through ServiceOverrides."
  - "DoubleHeight emits the plan's literal 1D 21 01; the cafe name is wrapped at columns/2 so it fits either way."
requirements-completed: []
duration: ~1 h (interrupted by a usage limit)
completed: 2026-11-20
status: complete
actuals:
  tasks: 3
  commits: 3
plan_head_before: 4774b2c9c48b8f85a5bffa0ed22bc2ea7e992281
plan_head_after: 9551b464e52de462bea8c4fb49a5a0127ca16b72
commits: 3
---

# Phase 6 Plan 03: Thermal receipt pipeline Summary

Own ESC/POS encoder and 42/32-column layout, raw-TCP and winspool printers, validated Receipts:* settings, and a never-failing print dispatcher with network-to-Windows fallback, all proven by byte-level and TcpListener tests.

## Tasks

| Task | Name | Commit |
| ---- | ---- | ------ |
| 1 | EscPosEncoder, text layout, renderer | 84ed73b |
| 2 | ReceiptBuilder and ReceiptService completion (tests, minutes detail) | 4153f7b |
| 3 | Printers, settings, print dispatcher | 9551b46 |

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Emoji encoded as two '?'**
- **Found during:** Task 1 tests
- **Issue:** the CP437 replacement fallback emitted one '?' per UTF-16 char of a surrogate pair.
- **Fix:** Normalise iterates runes and emits exactly one '?' for supplementary characters.
- **Commit:** 84ed73b

**2. [Rule 3 - Blocking] Task 2 service work was already largely done in plan 01**
- ReceiptService already handled all three target kinds, grouping and remaining. Task 2 only changed the session detail to rounded-up minutes and added the tests; the settings reader switch happened in task 3.

**3. [Rule 1 - Bug] CA1416 warning** on the Windows call site; guarded inside the lambda too. Build has 0 warnings.

## Notes
- Remaining on a receipt sums only the targets that checkout touched (a partially paid second order not in that payment is not listed).
- ReceiptService now takes ReceiptSettingsService; GetAsync signature unchanged.
- No csproj, Program.cs, Payments/Orders/Products edits; no migration.

## Human checks pending
- Print a real receipt on a thermal printer (network 9100 and Windows queue): confirm layout, CP437 accents and the cut.
- GS ! 01 magnifies width on standard ESC/POS printers (0x10 is double height). The plan and its acceptance test require the 01 byte; if the header looks double-width and wrong, change the byte in one place (EscPosEncoder.DoubleHeight).
- WindowsRawPrinter (winspool) is not exercised off Windows.

## Known Stubs
None.

## Threat Flags
None beyond the plan's threat model (T-06-11..15 mitigated: 5s timeout, control-char neutralisation, host/port validation, OS guard with try/catch).

## Verification
- `dotnet build Cafe.sln`: 0 warnings, 0 errors. `dotnet test Cafe.sln`: 810 passed, 0 failed.

## Self-Check: PASSED
