---
phase: 01-foundation
fixed_at: 2026-11-20T00:00:00Z
review_path: /Users/dylenquyn/Cafe/.planning/phases/01-foundation/01-REVIEW.md
iteration: 1
findings_in_scope: 8
fixed: 8
skipped: 0
status: all_fixed
---

# Phase 01: Code Review Fix Report

**Fixed at:** 2026-11-20
**Source review:** /Users/dylenquyn/Cafe/.planning/phases/01-foundation/01-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 8
- Fixed: 8
- Skipped: 0

**Verification:** run in the main checkout on branch `main` (no worktree). `dotnet build Cafe.sln` has 0 warnings and 0 errors. `dotnet test Cafe.sln` passed 114 of 114 (98 before the fixes).

## Fixed Issues

### WR-01: Setup ignores SetPasswordAsync result / swallowed DbUpdateException
**Files modified:** `src/Cafe.Server/Auth/AdminPasswordService.cs`, `src/Cafe.Server/Components/Pages/Setup.razor`, `tests/Cafe.Tests/SetupFlowTests.cs`, `tests/Cafe.Tests/Infrastructure/CafeWebApplicationFactory.cs`, `tests/Cafe.Tests/Infrastructure/FailingSaveDbContextFactory.cs` (new)
**Commit:** 3dd4714
**Applied fix:** The service now only treats a unique-constraint violation as "already configured". Other DB errors propagate. Setup.razor catches them, logs, and shows "Could not save the password..." when nothing was stored. Tests: the service rethrows, and the setup page shows the error.

### WR-02: Dashboard refresh loop dies on non-cancellation exceptions
**Files modified:** `src/Cafe.Server/Components/Pages/Home.razor`
**Commit:** 8d73a05
**Applied fix:** Each tick is wrapped in try/catch. A failure is logged, the last good data stays on screen, a "temporarily out of date" banner appears, and the next tick retries. DisposeAsync can no longer rethrow a DB error. No automated test: the loop only runs in an interactive circuit, and the project has no bUnit or browser harness.

### WR-03: No login throttling
**Files modified:** `src/Cafe.Server/Auth/LoginThrottle.cs` (new), `src/Cafe.Server/Program.cs`, `src/Cafe.Server/Components/Pages/Login.razor`, `README.md`, `tests/Cafe.Tests/LoginThrottleTests.cs` (new)
**Commit:** 7d805b6
**Applied fix:** A singleton in-memory throttle keyed by remote IP. 5 failures within 5 minutes lock that address for 5 minutes, with a clear message, and even the correct password is refused during lockout. A success resets the counter. No third-party packages. README documents the throttling and the plain-HTTP decision. Tests cover the unit logic (with a fake TimeProvider) and the HTTP flow.

### WR-04: Pages branch on error-message substring
**Files modified:** `src/Cafe.Server/Stations/ServiceResult.cs`, `StationService.cs`, `StationTypeService.cs`, `Components/Pages/Stations.razor`, `Components/Pages/StationTypes.razor`, tests
**Commit:** 76a608a
**Applied fix:** Added `FailureKind` (Validation or NotFound) to `ServiceResult`. Missing records return `ServiceResult.NotFound`. Pages check `Kind == NotFound`. A deleted station type during a station edit is now a Validation failure, so the form stays open and the edits are kept.

### WR-05: Hidden-field validation blocks submit silently
**Files modified:** `src/Cafe.Server/Stations/StationService.cs`, `src/Cafe.Server/Components/Pages/Stations.razor`, `tests/Cafe.Tests/StationServiceTests.cs`
**Commit:** 440b004
**Applied fix:** `Prepare` ignores validation errors for fields of the other kind. Changing Kind in the form clears the other kind's fields (`@bind-Value:after`). Tests cover PC with bad TV fields, and console with a bad machine ID.

### WR-06: AgentMachineId uniqueness app-level only
**Files modified:** `src/Cafe.Server/Data/CafeDbContext.cs`, `Data/Migrations/20261119190205_UniqueAgentMachineId.cs` and `.Designer.cs` (new), `CafeDbContextModelSnapshot.cs`, `Stations/StationService.cs`, `tests/Cafe.Tests/DataModelTests.cs`
**Commit:** b387193
**Applied fix:** The column has NOCASE collation and a unique index (SQLite allows many NULLs). A new migration was added. `Map` reports an AgentMachineId conflict on that field. A test checks the case-insensitive uniqueness and that multiple NULLs are allowed. Note: the migration will fail on an existing database that already holds duplicate machine IDs. The old app-level check should have prevented any.

### WR-07: Page handlers have no exception handling
**Files modified:** `src/Cafe.Server/Components/Pages/Stations.razor`, `StationTypes.razor`, `src/Cafe.Server/Stations/StationTypeService.cs`, `tests/Cafe.Tests/StationTypeServiceTests.cs`
**Commit:** 9ed2522
**Applied fix:** Load, save and delete handlers catch exceptions, log them, and show "Something went wrong. Try again." (or a load-failure message, with empty lists so the page does not hang on "Loading..."). `StationTypeService.DeleteAsync` maps only SQLite error 19 to "in use" and lets other errors propagate. The service behaviour has a test. The page-level catch blocks have no automated test because the handlers run only in interactive mode.

### WR-08: First LAN visitor can claim admin
**Files modified:** `src/Cafe.Server/Auth/SetupGateMiddleware.cs`, `src/Cafe.Server/appsettings.json`, `README.md`, `tests/Cafe.Tests/SetupFlowTests.cs`, `tests/Cafe.Tests/Infrastructure/CafeWebApplicationFactory.cs`
**Commit:** 58a2520
**Applied fix:** Before setup completes, GET and POST on `/setup` are served only to loopback requests. Non-loopback requests get a 403 with a friendly page explaining that setup must be done on the server PC at http://localhost:5080. The `Setup:AllowRemote` key (default false) opts out. It is documented in `appsettings.json` and the README. A null remote address is not treated as loopback. The test factory now injects a remote address through an `IStartupFilter` (loopback by default, overridable), so the existing tests still complete setup. Tests cover the remote GET, the remote POST, the AllowRemote opt-out, and a remote visitor after setup.

## Skipped Issues

None.

## Notes for the orchestrator

- Logic-level fixes needing human confirmation: WR-03 (throttle thresholds and lockout behaviour) and WR-08 (loopback gating). Both are covered by tests, but the thresholds were chosen from the approved guidance.
- Untracked files left alone: `.claude/launch.json`, `.planning/milestone.lock`, `.planning/state.json`. `.planning/phases/02-session-engine-pricing/` is also untracked and was not created by this run.

---

_Fixed: 2026-11-20_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
