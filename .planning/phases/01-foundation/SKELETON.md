# Walking Skeleton — Cafe Manager

**Phase:** 1
**Generated:** 2026-11-19

## Capability Proven End-to-End

A staff member opens the running server on the LAN, is sent to `/setup` on first run, chooses an admin password (a real SQLite write through an EF migration-created schema), and is redirected onward; the same stack later serves the login, CRUD and dashboard slices of Phase 1.

## Architectural Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Framework | ASP.NET Core Blazor Web App, net10.0, interactive Server render mode (no WebAssembly); login/setup are static SSR pages | Matches the project constraint (C#/.NET 10, Blazor server); static SSR is required so `HttpContext.SignInAsync` can run |
| Data layer | EF Core 10 + SQLite, `CafeDbContext` via `AddDbContextFactory`, migrations applied at startup with `Database.Migrate()`; money stored as TEXT via decimal-to-string conversion | Single local machine, no external DB; factory gives short-lived contexts safe for Blazor circuits; TEXT keeps decimals exact |
| Auth | Single shared admin password, hash in `Setting` table (`PasswordHasher<object>`), cookie auth with 30-day sliding persistent cookie, no Identity users table | Matches "one shared admin login" requirement; minimal moving parts |
| Deployment target | Local `dotnet run` on `http://0.0.0.0:5080`; `UseWindowsService()` already wired so Phase 7 only adds the installer | LAN-only cafe server; Windows service hosting is a no-op on macOS/dev |
| Directory layout | `src/Cafe.Server` (feature folders: `Data/`, `Auth/`, `Stations/`, `Dashboard/`, `Components/`), `src/Cafe.Shared` (enums/DTOs), `tests/Cafe.Tests` (xUnit + WebApplicationFactory) | Services testable without UI; shared contracts ready for the Phase 3 agent |

## Stack Touched in Phase 1

- [x] Project scaffold (solution, three projects, Directory.Build.props, xUnit test runner)
- [x] Routing — `/setup` (first run gate), then `/login`, `/`, `/stations`, `/station-types`
- [x] Database — real write (`AdminPasswordHash` setting) and real read (setup gate checks it on every request)
- [x] UI — static SSR setup form posting to the server and persisting to SQLite
- [x] Deployment — documented local full-stack run command (`dotnet run --project src/Cafe.Server`)

## Out of Scope (Deferred to Later Slices)

- Sessions, pricing, desk alerts (Phase 2)
- PC agent, SignalR hub, simulator (Phase 3)
- Samsung TV pairing and control (Phase 4)
- Customers, packages, POS, receipts, reports (Phases 5-7)
- HTTPS on the LAN, multi-user staff accounts, password reset UI (not in v1)

## Subsequent Slice Plan

Each later phase adds one vertical slice on top of this skeleton without altering its architectural decisions:

- Phase 1 (rest): login/logout, station types and stations CRUD, dashboard grid
- Phase 2: start, bill and stop timed sessions with desk alerts
- Phase 3: Windows lock-overlay agent over SignalR plus simulator
- Phase 4: Samsung TV pairing and power control
- Phase 5-7: customers/packages, POS/receipts, reports and Windows deployment
