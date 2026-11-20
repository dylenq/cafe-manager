---
phase: 05-customers-packages
plan: 03
subsystem: pricing
tags: [pricing, member-rate, rate-rules, sessions, blazor]
requires:
  - phase: 05-01
    provides: StationType.MemberHourlyRate, RateRule.AppliesTo, Session.CustomerId/IsMemberPricing, CustomerService.LoadLinkableAsync
provides:
  - PricingEngine.BaseRate and member/audience-aware Price (PricingRequest.IsMember)
  - Customer-linked StartPrepaidAsync / StartOpenEndedAsync overloads with start-time member snapshot
  - SessionCharges.OpenEndedAsync isMember parameter
  - /rate-rules Applies-to field and /station-types Member rate field
affects: [05-04, 05-05]
key-files:
  created:
    - tests/Cafe.Tests/PricingEngineMemberTests.cs
    - tests/Cafe.Tests/MemberSessionPricingTests.cs
    - tests/Cafe.Tests/MemberPricingPagesTests.cs
  modified:
    - src/Cafe.Server/Pricing/PricingModels.cs
    - src/Cafe.Server/Pricing/PricingEngine.cs
    - src/Cafe.Server/Pricing/RateRuleInput.cs
    - src/Cafe.Server/Pricing/RateRuleService.cs
    - src/Cafe.Server/Stations/StationTypeInput.cs
    - src/Cafe.Server/Stations/StationTypeService.cs
    - src/Cafe.Server/Sessions/SessionService.cs
    - src/Cafe.Server/Sessions/SessionCharges.cs
    - src/Cafe.Server/Dashboard/DashboardService.cs
    - src/Cafe.Server/Components/Pages/RateRules.razor
    - src/Cafe.Server/Components/Pages/StationTypes.razor
key-decisions:
  - "Member status is snapshotted into Session.IsMemberPricing at start and is the only input to later pricing (extension, estimate, stop)."
  - "Audience filtering happens before priority selection, in the engine's per-span applicable-rule filter."
status: complete
commits: 3
plan_head_before: de75d59a7659ef2115fb2cba30fda946a2bf5fc1
plan_head_after: aebb33a24c1605fab6de9b6e0255dc8ca0a163d1
actuals:
  tokens: 35000
  tasks: 3
  commits: 3
---

# Phase 5 Plan 03: Member pricing and customer-linked starts Summary

A linked member is charged StationType.MemberHourlyRate (falling back to HourlyRate) while a guest pays the base rate, with rate-rule audiences (Everyone, MembersOnly, GuestsOnly) filtered before priority selection and member status frozen per session at start.

## Tasks

| Task | Commit | What |
| ---- | ------ | ---- |
| 1 | a823178 | `PricingRequest.IsMember` (trailing optional), `PricingEngine.BaseRate`, audience filter; 6 engine tests |
| 2 | d5f93c5 | Customer overloads on prepaid/open-ended start, snapshot used by extend, stop, stop quote and dashboard estimate; 7 tests |
| 3 | aebb33a | `AppliesTo` and `MemberHourlyRate` on inputs, services (Enum.IsDefined validation) and both pages; 5 tests |

## Verification

`dotnet build Cafe.sln` 0 errors; `dotnet test Cafe.sln` 611 passed, 0 failed. No migration added.

## Deviations from Plan

None. One note: `CustomerService` has no `UpdateAsync` yet (plan 02 territory), so snapshot tests flip `Customer.IsMember` directly through the DbContext, which the plan allowed.

## Known Stubs

None.

## Human-only checks

- Visually confirm the new "Applies to" select on /rate-rules and "Member hourly rate (optional)" input on /station-types render sensibly (tests only assert server-rendered text).
- The dashboard UI for choosing a customer when starting a session is not part of this plan (service overloads only); requirements CUST-06 and PRIC-03 are left unchecked in REQUIREMENTS.md until the UI plan wires linking.

## Threat Flags

None. T-05-07 (inactive/missing customer rejected, no session created) and T-05-08 (snapshot immune to later IsMember flips) are covered by tests.

## Self-Check: PASSED
