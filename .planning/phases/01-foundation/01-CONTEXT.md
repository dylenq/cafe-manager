# Phase 1: Foundation — Context

**Source:** Decisions from the approved project plan (/Users/dylenquyn/.claude/plans/i-need-to-build-rustling-rainbow.md) and the requirements Q&A. Written 2026-11-19.

## Phase Boundary

Staff can log in and set up station types and stations, and they see a dashboard grid of every station. Sessions, agents, TVs, customers and POS come in later phases. The dashboard shows each station as Idle, or Offline when it has an agent or TV that hasn't connected yet. Session-driven statuses (in session, paused, expiring) arrive in Phase 2, so the status field and badge rendering should be easy to extend.

## Locked Decisions

### Solution layout
- `Cafe.sln` at the repo root. `global.json` pins SDK 10.0.404 (it already exists, so leave it alone).
- `src/Cafe.Server`: ASP.NET Core Blazor Web App on `net10.0`, interactive **server** render mode (no WebAssembly).
- `src/Cafe.Shared`: class library on `net10.0` for SignalR contracts and DTOs. It's created now but mostly empty (enums such as `StationKind` can live here).
- `tests/Cafe.Tests`: xUnit on `net10.0`, referencing Cafe.Server. Integration tests use `Microsoft.AspNetCore.Mvc.Testing` / `WebApplicationFactory`.
- `src/Cafe.Agent` (WPF) and `src/Cafe.AgentSim` are **not** created in this phase (Phase 3).
- Turn on nullable reference types and implicit usings. Put a `Directory.Build.props` at the root for shared settings (LangVersion latest, Nullable enable, TreatWarningsAsErrors false).

### Data
- EF Core with the SQLite provider (EF Core 10). `CafeDbContext` lives in `src/Cafe.Server/Data/`.
- The DB file is `cafe.db`. Its path comes from configuration (`ConnectionStrings:Cafe`), defaulting to `Data Source=cafe.db` beside the app. Add it to `.gitignore`.
- Use EF migrations (not EnsureCreated), applied automatically at startup with `Database.Migrate()`.
- Phase 1 entities:
  - `StationType`: Id, Name (unique, required), HourlyRate (decimal, stored as TEXT/decimal-safe), SortOrder.
  - `Station`: Id, Name (unique, required), StationTypeId (FK, required), Kind (enum `StationKind { PC, Console }`), AgentMachineId (string?, used for PC), TvIpAddress (string?, used for Console), TvMacAddress (string?), TvToken (string?, set during Phase 4 pairing), SortOrder, IsEnabled.
  - `Setting`: Key (PK string), Value (string). It stores the admin password hash and later settings (grace period, warning thresholds).
- Deleting a StationType that still has stations is blocked with a friendly error, not a cascade.
- Money is `decimal`. For SQLite, configure a value converter or `HasConversion<double>()`, whichever keeps decimal round-trips exact. Prefer storing as TEXT via a converter.

### Auth (single shared admin login)
- Cookie authentication (`AddAuthentication().AddCookie()`), with no ASP.NET Identity users table.
- First run: if no `AdminPasswordHash` setting exists, every request goes to `/setup`, where staff choose a password (minimum 8 characters, entered twice). Hash it with `Microsoft.AspNetCore.Identity.PasswordHasher<object>` (from `Microsoft.Extensions.Identity.Core`) and save it in the `Setting` table.
- `/login` asks for the password only. On success it issues a cookie with a 30-day sliding expiration and a persistent flag, so staff stay logged in across refreshes and browser restarts.
- `/logout` clears the cookie.
- Every page except `/setup` and `/login` requires authorization. Use `[Authorize]` via `_Imports.razor` or `AuthorizeRouteView` plus `RequireAuthorization` on the endpoints.
- Login/setup form posts must work under interactive server mode. Use static SSR form pages (`[ExcludeFromInteractiveRouting]` or a per-page render mode) so `HttpContext.SignInAsync` can run; this is the standard Blazor Web App pattern.
- Antiforgery stays enabled.

### Hosting / network
- `Program.cs` calls `builder.Host.UseWindowsService()` (`Microsoft.Extensions.Hosting.WindowsServices`). It's a no-op on macOS and in dev.
- Kestrel listens on `http://0.0.0.0:5080` by default (configurable), so tablets and phones on the LAN can reach it. No HTTPS on the LAN for v1; add a config note.

### UI
- Use the Bootstrap that ships with the Blazor template. **No third-party component library**, to keep dependencies minimal.
- Nav: Dashboard (`/`), Stations (`/stations`), Station Types (`/station-types`), Logout.
- Dashboard: a responsive card grid, one card per enabled station, ordered by SortOrder then Name. Each card shows the station name, type name, a kind icon/label (PC/Console) and a status badge. Phase 1 statuses are Idle and Offline. Offline means an agent or TV is configured but not yet connected; in Phase 1 every station with an AgentMachineId or TvIpAddress shows Offline and the rest show Idle. The rule sits behind an `IStationStatusProvider` so Phases 2–4 can replace it.
- CRUD pages are simple tables plus an add/edit form with validation (DataAnnotations: required names, HourlyRate ≥ 0, a valid IPv4 when TvIpAddress is set, a MAC format check when TvMacAddress is set). Kind=PC shows the AgentMachineId field; Kind=Console shows the TV fields.
- Layout works on a phone-width viewport (tablets at the desk).

### Testing
- xUnit integration tests through `WebApplicationFactory<Program>` against a temp SQLite file or in-memory SQLite (shared connection kept open).
- Cover at least: redirect to `/setup` on first run; setup then login works and a wrong password fails; unauthenticated requests to `/stations` redirect to `/login`; StationType/Station CRUD rules through the service layer (unique names, delete blocked when stations exist).
- Put CRUD logic in small services (`StationTypeService`, `StationService` in `src/Cafe.Server/Stations/`) so it's testable without UI. Blazor pages call those services.
- Verify commands run from the repo root: `dotnet build Cafe.sln`, `dotnet test Cafe.sln`.

## Claude's Discretion
- Exact component/file naming inside Components/Pages.
- Badge colors and icons (Bootstrap Icons via CDN or inline SVG are both fine).
- Whether to seed demo station types/stations in Development only. Seeding is allowed if it's dev-only.

## Deferred Ideas
- Sessions, pricing, desk alerts → Phase 2. PC agent / SignalR hub → Phase 3. TV pairing → Phase 4.
- HTTPS on LAN, multi-user staff accounts → not in v1.

## Environment Notes
- Dev machine: macOS arm64. The `dotnet` CLI is at `/usr/local/share/dotnet/dotnet` (add it to PATH: `export PATH="/usr/local/share/dotnet:$PATH"`). SDK 10.0.404 is pinned by global.json, and SDK 11 is also installed.
- Git commits must use the existing global identity (`dylenq`), with no Co-Authored-By trailers.
