---
phase: 01-foundation
plan: 01
subsystem: foundation
tags: [dotnet, blazor, efcore, sqlite, auth, walking-skeleton]
requires: []
provides:
  - Cafe.sln with Cafe.Server, Cafe.Shared, Cafe.Tests
  - CafeDbContext (Settings, StationTypes, Stations) with InitialCreate and AddStations migrations applied at startup
  - AdminPasswordService (HashKey, IsConfiguredAsync, SetPasswordAsync, VerifyAsync)
  - SetupGateMiddleware and static SSR /setup page
  - CafeWebApplicationFactory and FormPostHelper test infrastructure
  - Hosting config (UseWindowsService, Kestrel 0.0.0.0:5080)
affects: [01-02, 01-03, 01-04]
tech-stack:
  added: [EF Core 10.0.15 SQLite, Microsoft.Extensions.Hosting.WindowsServices 10.0.15, Mvc.Testing 10.0.15, dotnet-ef 10.0.15 (local tool)]
  patterns: [AddDbContextFactory with lazy connection string, static SSR forms via PageRenderMode, temp-SQLite WebApplicationFactory]
key-files:
  created:
    - Cafe.sln
    - Directory.Build.props
    - .gitignore
    - .config/dotnet-tools.json
    - README.md
    - src/Cafe.Shared/StationKind.cs
    - src/Cafe.Server/Program.cs
    - src/Cafe.Server/Data/CafeDbContext.cs
    - src/Cafe.Server/Data/Migrations/
    - src/Cafe.Server/Auth/AdminPasswordService.cs
    - src/Cafe.Server/Auth/SetupGateMiddleware.cs
    - src/Cafe.Server/Components/Pages/Setup.razor
    - src/Cafe.Server/Components/Layout/PublicLayout.razor
    - tests/Cafe.Tests/Infrastructure/CafeWebApplicationFactory.cs
    - tests/Cafe.Tests/Infrastructure/FormPostHelper.cs
    - tests/Cafe.Tests/SetupFlowTests.cs
    - tests/Cafe.Tests/DataModelTests.cs
  modified:
    - src/Cafe.Server/Components/App.razor
    - src/Cafe.Server/appsettings.json
    - src/Cafe.Server/Properties/launchSettings.json
key-decisions:
  - "Whitespace-only passwords are rejected by [Required] (AllowEmptyStrings false) with one message, 'Password is required and cannot be blank.', instead of a separate IValidatableObject rule"
  - "No DB default on Station.IsEnabled; the CLR initializer (true) is enough, and a SQL default of true would silently turn an explicit false into true on insert"
  - "Package versions pinned to 10.0.15 rather than floating 10.0.*"
requirements-completed: [ADMN-01, ADMN-02, ADMN-03]
duration: 12 min
completed: 2026-11-19
status: complete
actuals:
  tokens: 40000
  tasks: 3
  commits: 3
plan_head_before: b9730ee9efe952e47ca7177aba1a1081bff53110
plan_head_after: 49d67c841109f5be4c509911e6a92bb5e37c39a4
commits: 3
---

# Phase 1 Plan 01: Walking Skeleton Summary

Blazor interactive-server solution on net10.0 with EF Core SQLite migrations, a first-run /setup flow that stores a PasswordHasher hash behind a redirect gate, a StationType/Station schema with exact decimals, NOCASE unique names and restrict delete, and a reusable temp-SQLite WebApplicationFactory harness.

## Tasks

| Task | Name | Commit |
|------|------|--------|
| 1 | Tracer: scaffold and first-run /setup end-to-end | 95e12f2 |
| 2 | Data model: StationType, Station, StationKind | 1ddcd2c |
| 3 | Hosting config: Windows service, port 5080, README | 49d67c8 |

## Verification

- `dotnet build Cafe.sln` succeeds, 0 warnings in app code.
- `dotnet test Cafe.sln`: 18 passed (SetupFlowTests 10, DataModelTests 8 including theory cases).
- Smoke: built server on port 5080 with a temp `ConnectionStrings__Cafe` returned `302 http://127.0.0.1:5080/setup`; server stopped afterwards.
- No `EnsureCreated`; global.json unchanged; `dotnet list package` shows only Microsoft packages.
- Tracer gate: `<verify>` re-run end-to-end passed before expanding to Tasks 2 and 3.

## Deviations from Plan

**1. [Rule 1 - Bug] Whitespace-only password message** - `[Required]` already rejects whitespace-only strings, so the planned separate whitespace validation never ran. Replaced with one Required message. Files: Setup.razor, SetupFlowTests.cs. Commit 95e12f2.

**2. [Rule 1 - Bug] Station.IsEnabled default** - Initially configured `HasDefaultValue(true)`, which makes EF skip writing an explicit `false`. Removed before the migration was committed. Commit 1ddcd2c.

**3. [Rule 3 - Blocking] dotnet tool manifest location** - `dotnet new tool-manifest` wrote `dotnet-tools.json` at the root because `.config/` did not exist; moved it to `.config/dotnet-tools.json`.

**4. Static asset test path** - The template serves Bootstrap at `/lib/bootstrap/dist/css/bootstrap.min.css`, not `/bootstrap/bootstrap.min.css`; the gate-bypass test uses the real path. The gate logic (file extension in last segment) is as planned.

**5. Smoke command** - Run with `--no-launch-profile` so the Development launch profile does not interfere; both resolve to port 5080.

## Known Stubs

None. The template's sample pages (Home, Counter, Weather) and default nav remain untouched, as the plan said not to remove them yet; later plans replace them.

## Threat Flags

None beyond the plan's threat model. T-01-01 (setup re-run) mitigated in service, page and tests; T-01-02 mitigated (hash only, no logging); T-01-SC mitigated (Microsoft packages only).

## Self-Check: PASSED

Created files exist, commits 95e12f2, 1ddcd2c and 49d67c8 are in `git log`.
