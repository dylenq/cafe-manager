---
phase: 01-foundation
plan: 02
subsystem: auth
tags: [dotnet, blazor, cookie-auth, authorization, nav, antiforgery]
requires: [01-01]
provides:
  - Cookie auth scheme ".Cafe.Auth" (30-day sliding, persistent, HttpOnly, SameSite Lax)
  - Static SSR /login page and antiforgery-protected POST /logout
  - ReturnUrlHelper.Sanitize (local single-slash paths only)
  - Authorize-by-default via _Imports.razor with AllowAnonymous on Setup, Login, Error, NotFound
  - AuthorizeRouteView with RedirectToLogin for interactive navigation
  - Nav: Dashboard, Stations, Station Types, Logout
  - AuthTestExtensions.CreateAuthenticatedClientAsync for plans 03 and 04
affects: [01-03, 01-04]
tech-stack:
  added: []
  patterns: [AllowAnonymous attribute on static SSR pages, form-bound minimal API endpoint to force antiforgery validation, UseWhen for GET-only status-code pages]
key-files:
  created:
    - src/Cafe.Server/Auth/ReturnUrlHelper.cs
    - src/Cafe.Server/Components/Pages/Login.razor
    - src/Cafe.Server/Components/RedirectToLogin.razor
    - tests/Cafe.Tests/Infrastructure/AuthTestExtensions.cs
    - tests/Cafe.Tests/AuthTests.cs
  modified:
    - src/Cafe.Server/Program.cs
    - src/Cafe.Server/Components/Routes.razor
    - src/Cafe.Server/Components/_Imports.razor
    - src/Cafe.Server/Components/Pages/Setup.razor
    - src/Cafe.Server/Components/Pages/Error.razor
    - src/Cafe.Server/Components/Pages/NotFound.razor
    - src/Cafe.Server/Components/Layout/NavMenu.razor
    - src/Cafe.Server/Components/Layout/MainLayout.razor
  deleted:
    - src/Cafe.Server/Components/Pages/Counter.razor
    - src/Cafe.Server/Components/Pages/Weather.razor
key-decisions:
  - "Status-code re-execution (/not-found) now applies to GET/HEAD only; re-executing a rejected POST re-read the invalid form and threw instead of returning 400"
  - "Added RedirectToLogin.razor (not in plan file list) as the NotAuthorized content in AuthorizeRouteView"
  - "ReturnUrlHelper also rejects control characters in addition to the planned scheme/double-slash/backslash checks"
requirements-completed: [ADMN-01]
duration: 25 min
completed: 2026-11-19
status: complete
actuals:
  tokens: 45000
  tasks: 3
  commits: 3
plan_head_before: 5eb2b1af65cc52645f5ae2710dc0611c5b67e5f7
plan_head_after: 2d9fbd0132fe57f1ea5f8f92c39483f9b45ab97f
commits: 3
---

# Phase 1 Plan 02: Shared Admin Login Summary

Single shared-password login with a persistent 30-day sliding ".Cafe.Auth" cookie, antiforgery-protected logout, authorize-by-default Blazor pages with a local-only ReturnUrl, and a responsive nav (Dashboard, Stations, Station Types, Logout); ADMN-01 is fully delivered.

## Tasks

| Task | Name | Commit |
|------|------|--------|
| 1 | Cookie auth, /login, /logout and authorize-by-default routing | bd8b93b |
| 2 | Responsive nav and template sample cleanup | 99121b3 |
| 3 | Regression guard for the full auth journey | 2d9fbd0 |

## Verification

- `dotnet build Cafe.sln`: 0 warnings, 0 errors.
- `dotnet test Cafe.sln`: 42 passed (18 from plan 01, 24 auth test cases including theory rows).
- Manual curl run against a built server confirmed setup -> login -> Set-Cookie with expires about 30 days out; server stopped afterwards (no process left running).
- Greps: no DisableAntiforgery, no Identity types; ExpireTimeSpan FromDays(30) and SlidingExpiration true present.

## Deviations from Plan

**1. [Rule 1 - Bug] Failed POST re-executed through status-code pages** - A POST /logout without an antiforgery token returned 400, then `UseStatusCodePagesWithReExecute` re-ran the pipeline with the POST body and the antiforgery middleware threw an InvalidOperationException (500 instead of 400). Wrapped the status-code pages in `UseWhen` for GET/HEAD only. File: Program.cs. Commit bd8b93b.

**2. [Rule 3 - Blocking] Missing Components.Authorization using** - Without `@using Microsoft.AspNetCore.Components.Authorization`, `AuthorizeRouteView` rendered as a literal HTML element and always rendered its NotAuthorized child. Added the using to _Imports.razor. Commit bd8b93b.

**3. [Rule 3 - Blocking] RedirectToLogin component** - Plan said to redirect in the NotAuthorized template via NavigationManager; implemented as a small `RedirectToLogin.razor`. Commit bd8b93b.

**4. [Rule 2 - Hardening] ReturnUrl control characters** - Sanitize also falls back to "/" for any control character. Commit bd8b93b.

**5. Test sequencing** - The logout-with-token test needs the nav form, so it was added in the Task 2 commit instead of Task 1 (the plan's IAntiforgery fallback was dropped as the client cookie container overrides a manual antiforgery cookie). Task 1 kept the without-token test.

**6. Test environment limit** - The Testing environment does not map `/_framework/blazor.web.js` (returns 500 for the missing file), so the asset test asserts it is not redirected rather than 200; bootstrap CSS is asserted 200. Commit 2d9fbd0.

**7. Pipeline order** - `MapStaticAssets()` is called before the middleware registrations (it is an endpoint mapping, so order does not change behavior).

## Known Stubs

None. Home.razor remains the template placeholder and /stations and /station-types links 404 until plans 03 and 04, as the plan states.

## Threat Flags

None beyond the plan's threat model. T-01-05 (open redirect) mitigated by ReturnUrlHelper and tests; T-01-06 (CSRF) mitigated, logout without token returns 400; T-01-08 (unauthenticated access) mitigated, anonymous / redirects to /login; T-01-SC no packages added.

## Self-Check: PASSED

Created files exist; commits bd8b93b, 99121b3 and 2d9fbd0 are in `git log`.
