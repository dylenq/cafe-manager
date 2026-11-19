---
phase: 01-foundation
reviewed: 2026-11-19T00:00:00Z
depth: standard
files_reviewed: 60
files_reviewed_list:
  - .config/dotnet-tools.json
  - .gitignore
  - Cafe.sln
  - Directory.Build.props
  - README.md
  - src/Cafe.Server/Auth/AdminPasswordService.cs
  - src/Cafe.Server/Auth/ReturnUrlHelper.cs
  - src/Cafe.Server/Auth/SetupGateMiddleware.cs
  - src/Cafe.Server/Cafe.Server.csproj
  - src/Cafe.Server/Components/App.razor
  - src/Cafe.Server/Components/Layout/MainLayout.razor
  - src/Cafe.Server/Components/Layout/NavMenu.razor
  - src/Cafe.Server/Components/Layout/PublicLayout.razor
  - src/Cafe.Server/Components/Pages/Error.razor
  - src/Cafe.Server/Components/Pages/Home.razor
  - src/Cafe.Server/Components/Pages/Login.razor
  - src/Cafe.Server/Components/Pages/NotFound.razor
  - src/Cafe.Server/Components/Pages/Setup.razor
  - src/Cafe.Server/Components/Pages/StationTypes.razor
  - src/Cafe.Server/Components/Pages/Stations.razor
  - src/Cafe.Server/Components/RedirectToLogin.razor
  - src/Cafe.Server/Components/Routes.razor
  - src/Cafe.Server/Components/Shared/StationCard.razor
  - src/Cafe.Server/Components/Shared/StatusBadge.razor
  - src/Cafe.Server/Components/_Imports.razor
  - src/Cafe.Server/Dashboard/DashboardService.cs
  - src/Cafe.Server/Dashboard/DefaultStationStatusProvider.cs
  - src/Cafe.Server/Dashboard/IStationStatusProvider.cs
  - src/Cafe.Server/Dashboard/StationCardModel.cs
  - src/Cafe.Server/Data/CafeDbContext.cs
  - src/Cafe.Server/Data/CafeDbContextDesignTimeFactory.cs
  - src/Cafe.Server/Data/Migrations/20261119180305_InitialCreate.cs
  - src/Cafe.Server/Data/Migrations/20261119180420_AddStations.cs
  - src/Cafe.Server/Data/Setting.cs
  - src/Cafe.Server/Data/Station.cs
  - src/Cafe.Server/Data/StationType.cs
  - src/Cafe.Server/Program.cs
  - src/Cafe.Server/Properties/launchSettings.json
  - src/Cafe.Server/Stations/ServiceResult.cs
  - src/Cafe.Server/Stations/StationInput.cs
  - src/Cafe.Server/Stations/StationService.cs
  - src/Cafe.Server/Stations/StationTypeInput.cs
  - src/Cafe.Server/Stations/StationTypeService.cs
  - src/Cafe.Server/appsettings.Development.json
  - src/Cafe.Server/appsettings.json
  - src/Cafe.Shared/Cafe.Shared.csproj
  - src/Cafe.Shared/StationKind.cs
  - src/Cafe.Shared/StationStatus.cs
  - tests/Cafe.Tests/AuthTests.cs
  - tests/Cafe.Tests/Cafe.Tests.csproj
  - tests/Cafe.Tests/DashboardPageTests.cs
  - tests/Cafe.Tests/DashboardServiceTests.cs
  - tests/Cafe.Tests/DataModelTests.cs
  - tests/Cafe.Tests/Infrastructure/AuthTestExtensions.cs
  - tests/Cafe.Tests/Infrastructure/CafeWebApplicationFactory.cs
  - tests/Cafe.Tests/Infrastructure/FormPostHelper.cs
  - tests/Cafe.Tests/SetupFlowTests.cs
  - tests/Cafe.Tests/StationPagesTests.cs
  - tests/Cafe.Tests/StationServiceTests.cs
  - tests/Cafe.Tests/StationTypeServiceTests.cs
findings:
  critical: 0
  warning: 8
  info: 5
  total: 13
status: issues_found
---

# Phase 01: Code Review Report

**Reviewed:** 2026-11-19
**Depth:** standard
**Files Reviewed:** 60 (favicon.png skipped as binary)

## Summary

Core security design holds up. Passwords use `PasswordHasher` (PBKDF2). `[Authorize]` is applied globally via `_Imports.razor`, with explicit `[AllowAnonymous]` only on Login, Setup and Error. The return URL is sanitised against `//`, `\` and control characters. `/logout` requires an antiforgery token. Setup re-run is blocked by the `Settings` primary key. Station and station-type names are unique (NOCASE index), types use a restrict FK, and rates are stored as TEXT. DbContext is created per call through the factory.

No critical defects were found. The weaknesses are error handling in the interactive pages, a silent setup failure path, an unbounded login attempt rate, and a few data-integrity gaps enforced only in application code.

I did not read the test files or the StationCard, StatusBadge, NotFound and App components in depth. The findings come from the service, page and startup code.

## Warnings

### WR-01: Setup ignores the result of SetPasswordAsync and the service swallows every DbUpdateException

**File:** `src/Cafe.Server/Components/Pages/Setup.razor:64-69`, `src/Cafe.Server/Auth/AdminPasswordService.cs:34-42`
**Issue:** `SubmitAsync` discards the bool from `SetPasswordAsync`. The service catches all `DbUpdateException`s and returns false, not only the primary-key race. A locked DB, read-only file or full disk looks exactly like "already configured". The user is redirected to /login. The setup gate then bounces them back to /setup with no message, which loops forever. In the genuine race case, the second submitter also believes their password was saved.
**Fix:**
```csharp
// AdminPasswordService: only treat a unique violation as "already configured"
catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
{ return false; }

// Setup.razor
if (!await Passwords.SetPasswordAsync(Model!.Password))
{
    if (!await Passwords.IsConfiguredAsync()) { _error = "Could not save the password. Check the database file is writable."; return; }
}
```

### WR-02: Dashboard refresh loop dies silently on any non-cancellation exception, and DisposeAsync rethrows it

**File:** `src/Cafe.Server/Components/Pages/Home.razor:57-76, 78-95`
**Issue:** `RefreshLoopAsync` only catches `OperationCanceledException` and `ObjectDisposedException`. A transient `SqliteException` (for example a locked DB) from `GetCardsAsync` faults the fire-and-forget task. Live refresh stops with no indication to the user. `DisposeAsync` then does `await _loop`, and the catch there is also limited to those two exception types, so the original DB exception is rethrown during circuit teardown.
**Fix:** Wrap the loop body in a `try/catch (Exception ex) when (ex is not OperationCanceledException)`. Log the error and keep ticking. Optionally show a "stale data" flag.

### WR-03: No throttling or lockout on the login endpoint

**File:** `src/Cafe.Server/Components/Pages/Login.razor:56-78`
**Issue:** The app binds to `0.0.0.0:5080` over plain HTTP. There is a single shared password with only an 8-character minimum, and unlimited guesses are allowed. Anyone on the LAN can brute-force it, and the PBKDF2 cost is the only brake.
**Fix:** Add a simple in-memory failed-attempt counter keyed by remote IP with exponential delay or lockout. Alternatively use `AddRateLimiter` on the login POST.

### WR-04: Stations/StationTypes pages branch on a substring of the error message

**File:** `src/Cafe.Server/Components/Pages/Stations.razor:228`, `src/Cafe.Server/Components/Pages/StationTypes.razor:157`
**Issue:** `result.Error?.Contains("no longer exists")` is used as control flow. It is fragile if the text changes. It is also wrong for stations. `StationService.CheckRulesAsync` returns "That station type no longer exists. Choose another." when the selected type was deleted. That matches the substring, so the edit form closes and the admin's edits are discarded, although only the type field needed correcting.
**Fix:** Add a typed failure kind to `ServiceResult` (for example `NotFound` versus `Validation`) and branch on that.

### WR-05: Hidden-field validation can block submit with no visible message

**File:** `src/Cafe.Server/Components/Pages/Stations.razor:55-111`, `src/Cafe.Server/Stations/StationService.cs:121-125`
**Issue:** The form conditionally renders PC fields versus TV fields, but `DataAnnotationsValidator` validates the whole `StationInput`. If an admin types an invalid TV IP or MAC and then switches Kind to PC, the hidden field still fails the regex. The submit is silently rejected because the `ValidationMessage` is not rendered. The server-side `Prepare` also validates the fields that are about to be discarded.
**Fix:** When Kind changes, clear the fields of the other kind. In `Prepare`, validate only the fields relevant to the chosen Kind.

### WR-06: AgentMachineId uniqueness is app-level only, with a race and no DB constraint

**File:** `src/Cafe.Server/Stations/StationService.cs:149-156`, `src/Cafe.Server/Data/CafeDbContext.cs:34-41`
**Issue:** The case-insensitive duplicate check on machine ID is a check-then-insert with no unique index. Two concurrent saves can both pass and create duplicate agents, and `Map` would then report a misleading outcome. Phase 3 agent identification will rely on this being unique.
**Fix:** Add `e.HasIndex(s => s.AgentMachineId).IsUnique()` with `UseCollation("NOCASE")` on the column. SQLite permits multiple NULLs in a unique index. Extend `Map` to report the field that conflicted.

### WR-07: Page event handlers have no exception handling

**File:** `src/Cafe.Server/Components/Pages/Stations.razor:216-250`, `src/Cafe.Server/Components/Pages/StationTypes.razor:145-182`
**Issue:** `SaveAsync`, `DeleteAsync` and `LoadAsync` only guard `saving` with `finally`. A thrown exception (DB locked, I/O error) is unhandled in an interactive circuit. This tears down the circuit and shows the blazor-error-ui banner. `DeleteAsync` in StationTypeService also catches `DbUpdateException` and always reports "in use", masking real DB failures.
**Fix:** Catch `Exception` in the handlers, log it, and set `errorMessage = "Something went wrong. Try again."`. In `StationTypeService.DeleteAsync`, only map the FK violation (SqliteErrorCode 19) to the in-use message and rethrow or report other errors.

### WR-08: First visitor on the LAN can claim the admin account

**File:** `src/Cafe.Server/Auth/SetupGateMiddleware.cs:9-21`, `src/Cafe.Server/appsettings.json:12`
**Issue:** The server listens on all interfaces. Until setup completes, every page redirects to /setup, which accepts a password from anyone. This is inherent to the design, but nothing mitigates it. There is no setup token or loopback restriction, and the app may be started and left unattended on the cafe LAN.
**Fix:** Either restrict /setup to loopback until configured, or print a one-time setup code to the console or log that must be entered on the setup form. At minimum, document in the README that setup should be completed immediately.

## Info

### IN-01: Unused `returnUrl` parameter on /logout

**File:** `src/Cafe.Server/Program.cs:66`
**Issue:** The `[FromForm] string? returnUrl` parameter is never read. It exists only to trigger antiforgery validation. The code works but the intent is obscure.
**Fix:** Use `.RequireAntiforgery()` or `[FromForm]` on a dummy form model, or add a comment on why the binding is required.

### IN-02: Error page uses the authenticated MainLayout and contains scaffold text

**File:** `src/Cafe.Server/Components/Pages/Error.razor:1-30`
**Issue:** The page is `[AllowAnonymous]` but renders inside `MainLayout`, so anonymous users see the nav menu. It also shows the template's "Development Mode" guidance paragraph in production.
**Fix:** Add `@layout PublicLayout` and remove the development-mode text.

### IN-03: Hourly rate scale is unconstrained

**File:** `src/Cafe.Server/Stations/StationTypeInput.cs:9-10`, `src/Cafe.Server/Components/Pages/StationTypes.razor:99`
**Issue:** A value such as 12.3456 is stored exactly but displayed as `N2` (12.35). Billing in later phases may then differ from what the admin saw.
**Fix:** Round or validate to 2 decimal places in the service (`Math.Round(rate, 2)`), or reject more than 2.

### IN-04: Password hash never upgraded on SuccessRehashNeeded

**File:** `src/Cafe.Server/Auth/AdminPasswordService.cs:59-60`
**Issue:** `SuccessRehashNeeded` is treated as plain success, and the new hash is not persisted. This is harmless today, but it means iteration-count upgrades never take effect.
**Fix:** On `SuccessRehashNeeded`, recompute and update the Setting row.

### IN-05: Non-PC kinds are treated as Console, and the cookie has no revocation

**File:** `src/Cafe.Server/Stations/StationService.cs:127-137`, `src/Cafe.Server/Program.cs:33-45`
**Issue:** `Prepare` uses `else` for any `Kind` other than PC, so an out-of-range enum value is accepted as a Console. Separately, the 30-day sliding cookie has no security stamp, so a future password change will not invalidate existing sessions.
**Fix:** Validate `Enum.IsDefined`. When a password-change feature is added, embed a hash-derived stamp claim and validate it in `OnValidatePrincipal`.

---

_Reviewed: 2026-11-19_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
