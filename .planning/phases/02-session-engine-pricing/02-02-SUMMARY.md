---
phase: 02-session-engine-pricing
plan: 02
subsystem: pricing-config
tags: [blazor, efcore, validation, rate-rules, settings]
requires:
  - phase: 02-session-engine-pricing
    provides: RateRule entity, PricingEngine, SessionService quote path, SessionSettings read side (plan 01)
provides:
  - RateRuleService CRUD with server-side validation, RateRuleInput
  - Station type delete protection when rate rules reference it
  - SessionSettingsService.SaveAsync, SessionSettingsInput
  - /rate-rules and /settings pages (protected) with nav links
affects: [02-03, 02-04, 02-05]
actuals:
  tokens: 28000
  tasks: 3
  commits: 3
plan_head_before: 1187b7bdc3c8cbc86100db2fdb51650dd70e19ed
plan_head_after: 844df19010a0610e89fb36f055e3199642667e6f
tech-stack:
  added: []
  patterns:
    - Cross-field validation in the service, reported as FieldErrors keyed by input property
    - Pages show service FieldErrors next to fields in addition to DataAnnotations messages
key-files:
  created:
    - src/Cafe.Server/Pricing/{RateRuleInput,RateRuleService}.cs
    - src/Cafe.Server/Sessions/SessionSettingsInput.cs
    - src/Cafe.Server/Components/Pages/{RateRules,Settings}.razor
    - tests/Cafe.Tests/{RateRuleServiceTests,SessionPricingTests,SessionSettingsTests,RateRuleSettingsPagesTests}.cs
  modified:
    - src/Cafe.Server/Stations/{StationTypeService,ServiceResult}.cs
    - src/Cafe.Server/Sessions/SessionSettingsService.cs
    - src/Cafe.Server/Components/Layout/NavMenu.razor
    - src/Cafe.Server/Program.cs
key-decisions:
  - "Rate rule and settings validation lives in the services (UI-independent); pages only render FieldErrors."
  - "Settings save normalises warning minutes to unique descending order and stores them as an invariant comma list."
requirements-completed: [PRIC-02]  # SESS-09 is only configured here (grace period setting); the expiry action firing is a later plan
status: complete
---

# Phase 2 Plan 02: Rate rules and session settings Summary

Staff can now manage rate rules on /rate-rules and grace/warning settings on /settings, and a saved rule demonstrably changes a real prepaid quote (19:30 start, 60 min, 4000 fixed rule from 20:00 over a 6000 base quotes 5000.00).

## Accomplishments

- `RateRuleService` (scoped): list (priority desc, then id), get, create, update, delete, set-enabled. Rejects Value <= 0, no day, start == end, blank/over-100 name, unknown station type, Multiplier > 100, Priority outside 0..1000. Overnight windows (end < start) are valid. Missing records return NotFound.
- `StationTypeService.DeleteAsync` refuses types referenced by rate rules ("used by N rate rule(s)"), including the FK-race path.
- `SessionSettingsService.SaveAsync` validates grace 0..30 and warning list (ints 1..60), normalises to unique descending, upserts both Setting rows in one SaveChanges.
- `/rate-rules` page: table with overnight marker, enable toggle, inline delete confirmation; form with day checkboxes, time inputs, kind, value, priority. `/settings` page with grace and warning inputs. Both behind `[Authorize]`; nav has Rate Rules and Settings links.
- Tests: 226 total pass (181 before, 45 new).

## Task Commits

1. Task 1 (RateRuleService, type protection, quote tests): 09ee32b
2. Task 2 (/rate-rules page, nav): 34f324e
3. Task 3 (settings save, /settings page): 844df19

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] FakeTimeProvider cannot move backwards**
- **Found during:** Task 1 (SessionPricingTests)
- **Issue:** the plan said to set the factory clock to 2026-03-04 19:30 UTC, but the factory clock starts at the real current instant (plan 01 decision) and `SetUtcNow` throws when going back.
- **Fix:** the test advances the clock to the next 19:30 UTC; all rules cover every day so the date is irrelevant.
- **Files:** tests/Cafe.Tests/SessionPricingTests.cs
- **Commit:** 09ee32b

**2. [Rule 2 - Missing critical] FK violation on a station type deleted mid-save**
- **Fix:** added `InputValidation.IsForeignKeyViolation` and mapped it to a validation failure on create/update, mirroring the Phase 1 deleted-type behaviour.
- **Files:** src/Cafe.Server/Stations/ServiceResult.cs, RateRuleService.cs
- **Commit:** 09ee32b

Note: `[Authorize]` is already applied globally in `_Imports.razor`; the explicit attribute on the new pages is redundant but satisfies the plan's acceptance grep.

## Authentication gates

None.

## Known Stubs

None.

## Threat Flags

None. T-02-07 (auth on both pages, anonymous redirect tests), T-02-08 (server-side rule validation) and T-02-09 (strict warning list parsing, fallback to defaults on malformed stored data) are implemented and tested.

## Human checks pending

- Optional manual smoke: run the server, add a rate rule on /rate-rules (check the time inputs and day checkboxes in a real browser and that the form saves), then edit grace/warning on /settings. The form interaction paths (button handlers, InputDate time inputs) have no browser test harness and were verified only by build and server-rendered tests.

## Self-Check: PASSED

- All created files exist; commits 09ee32b, 34f324e, 844df19 exist.
- `dotnet build Cafe.sln` 0 warnings, 0 errors; `dotnet test Cafe.sln` 226/226 pass.
