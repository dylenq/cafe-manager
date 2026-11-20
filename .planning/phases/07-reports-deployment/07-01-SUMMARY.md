---
phase: 07-reports-deployment
plan: 01
subsystem: reports
tags: [reports, csv, aggregation, timezone, blazor]
requires:
  - phase: 06-pos-receipts
    provides: Payment, PaymentAllocation, Order/OrderLine, CheckoutService, Pos:BillingStartUtc
provides:
  - ReportService.GetAsync (DB loader) and pure static ReportService.Build
  - ReportRange/ReportPreset (presets, Monday weeks, ISO labels, DST-safe UTC bounds, 400-day cap)
  - CsvWriter with formula-injection guard
  - AddReportServices / MapReportEndpoints, GET /reports/export/days
  - /reports page (tracer body: this-week by-day table)
affects: [07-02 reports UI and remaining exports, 07-03 deployment]
tech-stack:
  added: []
  patterns: [load slim rows then filter/sum in memory (SQLite TEXT decimals), pure builder over fact records]
key-files:
  created:
    - src/Cafe.Server/Reports/ReportModels.cs
    - src/Cafe.Server/Reports/ReportRange.cs
    - src/Cafe.Server/Reports/ReportService.cs
    - src/Cafe.Server/Reports/CsvWriter.cs
    - src/Cafe.Server/Reports/ReportServiceCollectionExtensions.cs
    - src/Cafe.Server/Components/Pages/Reports.razor
    - tests/Cafe.Tests/Infrastructure/ReportTestBase.cs
    - tests/Cafe.Tests/ReportsTracerTests.cs
    - tests/Cafe.Tests/ReportRangeTests.cs
    - tests/Cafe.Tests/CsvWriterTests.cs
    - tests/Cafe.Tests/ReportServiceTests.cs
  modified:
    - src/Cafe.Server/Program.cs
key-decisions:
  - "Cash basis: revenue is the sum of allocations, dated by Payment.AtUtc in the cafe zone"
  - "Product revenue and quantity: allocations to an order are aggregated per order and shared across lines pro rata to LineTotal, remainder to the last line"
  - "Voided orders are excluded by the loader; their stray allocations still count in Collected but never in products or Charged"
  - "Orders and package sales are not gated by BillingStartUtc in Charged, only sessions are (as specified)"
status: complete
commits: 3
plan_head_before: 419acd6f7b97e39784f8b4a9e3e350feda11b461
plan_head_after: 9aa3b42baa497688a805f126f6db783a08476048
actuals:
  tasks: 3
  commits: 3
duration: single session
completed: 2026-11-20
---

# Phase 7 Plan 01: Report engine Summary

Cash-basis revenue report engine (pure `ReportService.Build` plus a DB loader), cafe-local range handling and a formula-safe CSV writer, proven by a tracer from a real checkout to the /reports page and `/reports/export/days`.

## Tasks

| Task | Commit | Result |
|------|--------|--------|
| 1 Tracer: checkout to by-day page to CSV | 758e143 | Real checkout (Cash 56000) appears in the service result, `/reports` HTML and the CSV; anonymous export redirects to /login; bad dates return 400 |
| 2 Range presets, ISO weeks, CSV escaping | fb6fd1b | All presets, Monday weeks, ISO week-year labels, DST bounds (23h/25h/24h), 400-day cap; CSV neutralises text cells starting with = + - @ tab CR, numbers and dates untouched |
| 3 Full aggregation | 9aa3b42 | Weeks, station type (first segment), products, categories, methods, session tiles, Charged (unpaid); loader excludes voided orders and honours BillingStartUtc |

## Verification

- `dotnet build Cafe.sln`: 0 warnings, 0 errors.
- `dotnet test Cafe.sln`: 955 passed, 0 failed (includes 72 new report, range and CSV tests; no regressions in Phases 1-6).

## Deviations from Plan

**1. [Rule 3 - Blocking] Reports.razor injected property named `Reports`**
- Collided with the generated component class name (CS0542). Renamed the injection to `ReportSvc`.

**2. [Rule 2 - Hardening] Export endpoint range validation routed through `ReportRange.TryResolve`**
- Task 1 implemented a bare from/to check; Task 2 switched the endpoint to `TryResolve` so the 400-day cap (T-07-03) also protects the export, not only the page.

**3. Plan name adaptations (real code wins)**
- `OpenTabsService.BillingStartKey` reused for the `Pos:BillingStartUtc` key.
- The unused `TimeProvider` constructor parameter on `ReportService` was dropped; callers pass "today".
- `ReportInput` optional members default to null so the tracer-shape calls (`new ReportInput(payments)`) keep compiling.
- `ReportRange` also exposes `Resolve` (throwing) in addition to `TryResolve`.

## Threat model

T-07-01 (CSV formula injection), T-07-02 (export requires auth, tested with an anonymous client) and T-07-03 (400-day cap, 400 on bad input) are mitigated and tested. No new packages (T-07-SC).

## Requirements

REPT-01 and REPT-02 are computable and the day section is exportable, but the page still shows only the tracer by-day table and only `/reports/export/days` exists. The range picker, remaining sections, bar chart and other exports are Plan 02, so REQUIREMENTS.md was intentionally not updated.

## Known Stubs

None. `Reports.razor` is deliberately a thin tracer body (this week, by-day table only) that Plan 02 replaces; the route and service call are stable. No nav link was added (Plan 02 or the orchestrator can add one; NavMenu.razor untouched to avoid conflicts with Plan 06-04).

## Human checks

None required; everything was verified by automated tests on macOS.

## Self-Check: PASSED

All created files exist and commits 758e143, fb6fd1b and 9aa3b42 are on branch `worktree-agent-aee2fc652f4827f29`.
