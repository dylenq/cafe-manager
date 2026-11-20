---
phase: 05-customers-packages
plan: 05
subsystem: customers-ui
tags: [blazor, customers, packages, dashboard]
requires:
  - phase: 05-02
    provides: CustomerService, PackageService, sell and adjust
  - phase: 05-03
    provides: member snapshot pricing
  - phase: 05-04
    provides: ExtendFromBalanceAsync, StopQuote.RefundMinutes
provides:
  - /customers, /customers/{id}, /packages pages and nav links
  - CustomerPicker shared component
  - StationCard customer, balance start, extend from balance, refund note
affects: []
tech-stack:
  added: []
  patterns: [RateRules.razor page pattern, request-counter guard for stale searches]
key-files:
  created:
    - src/Cafe.Server/Components/Pages/Customers.razor
    - src/Cafe.Server/Components/Pages/CustomerDetail.razor
    - src/Cafe.Server/Components/Pages/Packages.razor
    - src/Cafe.Server/Components/Shared/CustomerPicker.razor
    - src/Cafe.Server/Customers/CustomerPick.cs
    - src/Cafe.Server/Customers/PhoneDisplay.cs
    - tests/Cafe.Tests/CustomerPagesTests.cs
    - tests/Cafe.Tests/CustomerDashboardUiTests.cs
  modified:
    - src/Cafe.Server/Components/Shared/StationCard.razor
    - src/Cafe.Server/Components/Layout/NavMenu.razor
    - src/Cafe.Server/Customers/BalanceService.cs
key-decisions:
  - "Phone is shown grouped (3-3-rest) for display only; the stored digits are never altered."
  - "Insufficient balance on Extend from balance shows an explicit 'Pay for the extension instead (N min)' choice; there is no silent fallback. The label shows minutes, not a price, because no pre-quote API exists for an extension."
  - "BalanceService gained GetUsableMinutesForStationAsync (additive) so the card does not need the station type id."
requirements-completed: [CUST-01, CUST-02, CUST-03, CUST-04, CUST-05, CUST-06, PRIC-03]
status: complete
commits: 3
plan_head_before: 708aee483525e8dd9c5e7513c26c5ddfe7420fd2
plan_head_after: ec0442d88d131d6af52b12311d871745529603ef
actuals:
  tokens: 60000
  tasks: 3
  commits: 3
duration: ~35 min
completed: 2026-11-20
---

# Phase 5 Plan 05: Customers and packages UI Summary

Staff can now manage customers and packages and run customer, member and balance sessions entirely from the browser: three authenticated pages, a dashboard customer picker, and balance-aware start, extend and stop on the station card.

## Commits

- 7f79e02 feat(05-05): customers, customer detail and packages pages with nav links
- bab38b0 feat(05-05): dashboard card customer picker, balance start, extend from balance, refund note
- ec0442d test(05-05): customer, package page and dashboard card smoke tests

## What was built

- `/customers`: search (button or Enter), Show inactive, add/edit form with field errors (phone conflicts on the field), Deactivate/Reactivate with confirm, "Showing up to 50" caption, link to detail.
- `/customers/{id}`: balance per bucket and total, Sell package (active packages, Cash/Card), Manual adjustment (bucket, signed minutes, required note), purchases, ledger, 20 recent sessions; friendly not-found message with HTTP 200.
- `/packages`: full CRUD, activate/deactivate, delete-if-unsold (service message shown).
- `CustomerPicker`: debounce-free oninput search guarded by a request counter, max 8 active results, chip with Member badge, Quick add (name + phone) with field errors.
- `StationCard` (idle): optional picker; starts go through the customer overloads; "Start from balance" block appears when usable minutes > 0. (active): customer name, Member badge for member-priced sessions, "Extend from balance" for Balance sessions with the explicit paid fallback, "Refund N min to balance" in the stop confirmation and in the stop notice. TV, PC-menu and alert markup untouched.

## Deviations from Plan

None - plan executed as written. The additive `BalanceService.GetUsableMinutesForStationAsync` and the `PhoneDisplay` / `CustomerPick` helpers were anticipated by the plan text.

## Known Stubs

None.

## Threat Flags

None. T-05-12 (Authorize on all pages, anonymous redirect theory), T-05-13 (no MarkupString, encoded-markup test) and T-05-15 (`busy`/`saving`/`_busy` guards) are implemented and tested.

## Human checks pending

With the server running (`dotnet run --project src/Cafe.Server`): create a customer, sell a 120-minute package (Cash), pick the customer on an idle dashboard card, Start from balance 30, Stop and confirm the refund line, then open /customers/{id} and confirm the ledger shows purchase, debit and refund. Also exercise the interactive-only flows that HTTP prerender tests cannot reach: picker search/quick add, the insufficient-balance "Pay for the extension instead" choice. Non-blocking.

## Verification

`dotnet build Cafe.sln` clean; `dotnet test Cafe.sln` 740 passed, 0 failed.

## Self-Check: PASSED

Created files exist and commits 7f79e02, bab38b0, ec0442d are present.
