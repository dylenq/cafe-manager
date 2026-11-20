---
phase: 07-reports-deployment
plan: 02
subsystem: reports
tags: [reports, csv, blazor, svg, export]
requires:
  - phase: 07-reports-deployment
    provides: ReportService, ReportRange, CsvWriter, days export (plan 01)
provides:
  - ReportExport whitelist with 7 CSV sections
  - /reports page with range picker, tiles, tables and inline SVG chart
  - Reports nav link
affects: [07-03 deployment]
tech-stack:
  added: []
  patterns: [whitelist dictionary for export sections, inline SVG chart styled by CSS variables, range state in the query string]
key-files:
  created:
    - src/Cafe.Server/Reports/ReportExport.cs
    - src/Cafe.Server/Components/Shared/RevenueBarChart.razor
    - src/Cafe.Server/Components/Shared/RevenueBarChart.razor.css
    - src/Cafe.Server/Components/Pages/Reports.razor.css
    - tests/Cafe.Tests/ReportExportTests.cs
    - tests/Cafe.Tests/ReportPageTests.cs
  modified:
    - src/Cafe.Server/Reports/ReportServiceCollectionExtensions.cs
    - src/Cafe.Server/Components/Pages/Reports.razor
    - src/Cafe.Server/Components/Layout/NavMenu.razor
key-decisions:
  - "Export sections resolve only through a case-sensitive whitelist; section is never used for paths or queries"
  - "Picker state lives in ?preset= or ?from=&to= so refresh and shared links keep the range"
  - "Chart axis labels use Razor's <!text> escape because <text> is a Razor keyword"
status: complete
commits: 2
plan_head_before: b4304c55e2bd43a67f68aafb7e9f5411a9819bdc
plan_head_after: bdb9d9d1f62b022f0e669d936209504c0d0ef3bb
actuals:
  tasks: 2
  commits: 2
duration: single session
completed: 2026-11-20
---

# Phase 7 Plan 02: Reports UI and export Summary

Finished /reports (presets plus custom range in the URL, summary tiles including a distinct Charged (unpaid) tile, six tables, inline SVG day chart) and `/reports/export/{section}` for seven whitelisted sections, with a Reports nav link.

## Tasks

| Task | Commit | Result |
|------|--------|--------|
| 1 CSV export for all sections | 334af82 | 7 sections with exact headers, BOM, CRLF, filename; unknown section 404, bad/over-long range 400, anonymous redirected; formula and comma names escaped |
| 2 /reports page, chart, nav | bdb9d9d | Range picker, tiles, tables with empty states, 7 Export CSV links carrying the resolved range, SVG chart, escaping and anonymous tests |

## Verification

- `dotnet build Cafe.sln`: 0 warnings, 0 errors.
- `dotnet test Cafe.sln`: 1012 passed, 0 failed (before the final two-line assertion addition; ReportPageTests rerun: 5 passed).
- No CDN or script src in the page or chart.

## Deviations from Plan

**1. [Rule 3 - Blocking] Razor `<text>` tag and date inputs**
- `<text>` in the SVG is a Razor keyword; used the `<!text>` escape (asserted in a test that real `<text` is emitted).
- `type="date"` cannot `@bind` a string; used `value` plus `@onchange`.

**2. [Rule 3 - Test setup]** Anonymous tests first create an authenticated client because first-run setup otherwise redirects to /setup; export tests reuse one client per test.

**3. Dark mode:** the app has no dark-mode mechanism (no `prefers-color-scheme` or `data-bs-theme`); styles use Bootstrap CSS variables with fallbacks, so they follow a theme if one is added later.

**4. Nav placement:** Reports is placed after POS/before Products (additive), not literally after Sessions; Sessions is followed by POS.

## Threat model

T-07-04 (section whitelist, 404), T-07-05 (Razor encoding; page test with a script-tag product), T-07-02 (anonymous redirect on page and every export) mitigated and tested. No new packages.

## Requirements

REPT-01 and REPT-02 marked complete in REQUIREMENTS.md.

## Known Stubs

None.

## Human checks

- Visually confirm the chart and tiles look right in the browser (not verified by tests).
- Confirm the Apply button / preset change navigates in the interactive circuit (tests only cover server-rendered GETs).

## Self-Check: PASSED

All created files exist; commits 334af82 and bdb9d9d are on main.
