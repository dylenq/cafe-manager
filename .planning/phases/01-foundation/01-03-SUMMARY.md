---
phase: 01-foundation
plan: 03
subsystem: stations
tags: [dotnet, blazor, efcore, crud, validation, services]
requires: [01-01, 01-02]
provides:
  - ServiceResult / ServiceResult<T> with friendly Error and per-field FieldErrors
  - StationTypeService and StationService (List/Get/Create/Update/Delete), scoped in DI
  - StationTypeInput and StationInput DataAnnotations models (IPv4 and MAC regexes)
  - /station-types and /stations Blazor CRUD pages (interactive server, authorized by default)
affects: [01-04]
tech-stack:
  added: []
  patterns: [service-layer validation via Validator.TryValidateObject mirroring the form, DbContextFactory short-lived contexts per call, two-step delete without JS]
key-files:
  created:
    - src/Cafe.Server/Stations/ServiceResult.cs
    - src/Cafe.Server/Stations/StationTypeInput.cs
    - src/Cafe.Server/Stations/StationInput.cs
    - src/Cafe.Server/Stations/StationTypeService.cs
    - src/Cafe.Server/Stations/StationService.cs
    - src/Cafe.Server/Components/Pages/StationTypes.razor
    - src/Cafe.Server/Components/Pages/Stations.razor
    - tests/Cafe.Tests/StationTypeServiceTests.cs
    - tests/Cafe.Tests/StationServiceTests.cs
    - tests/Cafe.Tests/StationPagesTests.cs
  modified:
    - src/Cafe.Server/Program.cs
key-decisions:
  - "IPv4/MAC regexes allow empty or whitespace-only so optional fields validate in the form; services trim and store null"
  - "Station AgentMachineId uniqueness is checked with lower() in SQL because the column has no NOCASE collation"
  - "The no-station-types hint on /stations is shown at page level (not only inside the form) so it is visible before Add is clicked"
requirements-completed: [ADMN-02, ADMN-03]
duration: 20 min
completed: 2026-11-19
status: complete
actuals:
  tokens: 32000
  tasks: 3
  commits: 3
plan_head_before: b97dabd0575f45f7f594db0831f4b066608aa344
plan_head_after: 71e476b8c937c8f771bbfebdc0d655765f2dea96
commits: 3
---

# Phase 1 Plan 03: Station Types and Stations CRUD Summary

Station type and station management through testable EF Core services (case-insensitive unique names, delete blocked with a friendly count message, kind-dependent fields, MAC normalised to AA:BB:CC:DD:EE:FF, TvToken never touched) and two protected Blazor CRUD pages; ADMN-02 and ADMN-03 are delivered.

## Tasks

| Task | Name | Commit |
|------|------|--------|
| 1 | StationTypeService and StationService with validation, uniqueness and delete-blocking rules | 1567026 |
| 2 | Station Types page (/station-types) | 4575ccb |
| 3 | Stations page (/stations) and page tests | 71e476b |

## Verification

- `dotnet build Cafe.sln`: 0 warnings, 0 errors.
- `dotnet test Cafe.sln`: 85 passed (42 earlier; 37 service tests, 6 page tests added).
- Acceptance greps: no CafeDbContext in Components, no TvToken in StationInput or Stations.razor, both AddScoped registrations present.
- The plan's human-check (manual browser walk-through) was not run; page rendering, auth redirect and service rules are covered by automated tests, but the interactive form behaviour (Kind toggle, two-step delete clicks) was not exercised in a browser.

## Deviations from Plan

**1. [Rule 3 - Blocking] Injected service name clashed with page class** - `@inject StationTypeService StationTypes` in StationTypes.razor produced CS0542 (member name equals enclosing type). Renamed to `TypeService` (and `StationSvc`/`TypeSvc` in Stations.razor). Commit 4575ccb.

**2. [Rule 1 - Bug] Whitespace-only optional fields failed form validation** - A strict regex rejects "  " even though the service stores it as null. Regexes allow surrounding whitespace and empty. Commit 1567026.

**3. [Rule 3 - Blocking] No-types hint hidden while form closed** - The plan test (d) needs the guidance link on a fresh GET, but the form is closed initially. Hint moved to a page-level alert. Commit 71e476b.

**Total deviations:** 3 auto-fixed. **Impact:** none on scope.

## Known Stubs

None.

## Threat Flags

None beyond the plan's threat model. T-01-09 (inputs validated in service and form, EF parameterised), T-01-10 (anonymous requests 302 to /login, tested), T-01-11 (restrict delete plus pre-check, tests assert rows remain), T-01-12 (TvToken absent from form/input) mitigated; T-01-SC no packages added.

## Self-Check: PASSED

Created files exist; commits 1567026, 4575ccb and 71e476b are in `git log`; build and 85 tests pass.
