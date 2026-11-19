# Phase 2: Session Engine & Pricing — Context

**Source:** The approved project plan (/Users/dylenquyn/.claude/plans/i-need-to-build-rustling-rainbow.md), the requirements Q&A, and the Phase 1 codebase. Written 2026-11-20. The user said to continue without a separate discuss step. Decisions marked (default) are Claude's recommended defaults and can be changed later.

## Phase Boundary

Staff can run and bill timed sessions on any station: start, pause, resume, extend, move, stop. Pricing comes from the station type plus time-of-day rate rules. The dashboard shows live countdowns, and the desk gets visible and audible alerts. After the grace period an expiry action fires, and every action lands in an audit trail.

Out of scope:
- PC lock/unlock (Phase 3) and TV power-off (Phase 4). The expiry action is a seam (interface) whose default implementation only logs.
- Customers, member pricing (PRIC-03) and packages (Phase 5).
- Payments recording (Phase 6). The final charge is computed and stored, but no payment record is created.

## Existing Code to Build On (Phase 1)
- `src/Cafe.Server/Data/CafeDbContext.cs`: registered with `AddDbContextFactory`; use the factory everywhere. Settings live in the `Setting` table (key/value).
- `src/Cafe.Server/Stations/*Service.cs` and `ServiceResult`: follow the same service plus `ServiceResult` pattern for the new services.
- `src/Cafe.Server/Dashboard/IStationStatusProvider.cs`, `DefaultStationStatusProvider.cs`, `DashboardService.cs`, `Components/Pages/Home.razor` (5s PeriodicTimer), `Components/Shared/StationCard.razor`, `StatusBadge.razor`.
  - `StationStatus` (in Cafe.Shared) already has Idle, InSession, Paused, Expiring and Offline.
  - Replace the default provider with a session-aware one. Keep the Offline rule for stations with an agent or TV configured but not connected; for those, session status takes precedence when a session is active.
- Tests: `tests/Cafe.Tests` with `CafeWebApplicationFactory` (Testing environment, temp SQLite) and the auth helpers in `Infrastructure/`.

## Locked Decisions

### Time
- Inject `TimeProvider` (built into .NET 8+) everywhere time is read: `TimeProvider.System` in production, `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` (first-party Microsoft) in tests. Never call `DateTime.Now` or `UtcNow` directly.
- Store all timestamps as UTC (`DateTimeOffset` or UTC `DateTime`). Rate rules are evaluated in cafe local time from config `Cafe:TimeZone` (IANA or Windows id; empty means the server's local zone).

### Data model (new entities and EF migration)
- `Session`:
  - Id, StationId (current station), Mode (`SessionMode { Prepaid, OpenEnded }`) and Status (`SessionStatus { Active, Paused, Grace, Ended }`).
  - StartedAtUtc, PurchasedMinutes (int?, prepaid only; extensions add to it) and EndedAtUtc?.
  - EndReason (`SessionEndReason { Stopped, Expired }`?), ChargedAmount (decimal, stored the same way as HourlyRate) and Notes?.
  - At most one non-Ended session per station. Enforce it in the service and with a filtered unique index on StationId WHERE Status != Ended.
- `SessionSegment`:
  - Id, SessionId, StationId, StartUtc, EndUtc?.
  - One row per *active* interval. Pause closes the open segment and resume opens a new one. Move closes the segment on the old station and opens one on the new station.
  - Active (billable, countdown-consuming) time = the sum of segment durations.
- `SessionEvent` (audit trail):
  - Id, SessionId, StationId, Type and AtUtc.
  - Type is `SessionEventType { Started, Paused, Resumed, Extended, Moved, Stopped, WarningReached, Expired, GraceElapsed, ExpiryActionFired }`.
  - Details: a short human-readable string, e.g. "Extended by 30 min", "Moved PC-01 → PC-03", "Charge $12.50".
- `RateRule`:
  - Id, Name, StationTypeId? (null = all types) and DaysOfWeek (flags enum, Mon–Sun).
  - StartTime and EndTime (`TimeOnly`, local). EndTime ≤ StartTime means the window crosses midnight into the next day.
  - Kind (`RateRuleKind { FixedHourlyRate, Multiplier }`), Value (decimal), Priority (int; higher wins when windows overlap) and IsEnabled.
- Settings (keys in the `Setting` table, edited on a Settings page):
  - `Sessions:GracePeriodMinutes`, default 2.
  - `Sessions:WarningMinutes`, default "10,5,1". The largest value is the start of the warning window; all values are PC warning points for Phase 3.

### Pricing (PRIC-01, PRIC-02)
- `Pricing/PricingEngine` is a pure, stateless class with no DB or clock access.
  - Input: a list of priced intervals (UTC start/end plus the station type's base hourly rate and id), the enabled rate rules, and the cafe time zone.
  - Output: a total plus a per-window breakdown.
  - It splits each interval at every rule-window boundary and at local midnight. Each slice's effective hourly rate is chosen like this:
    1. Take the highest-Priority enabled rule matching the slice's station type, local day-of-week and local time-of-day. Ties go to the lowest Id.
    2. If no rule matches, use the base rate.
    3. A Multiplier rule means base × Value; a FixedHourlyRate rule means Value.
- Billing granularity (default): bill by the second internally, then round the session total **up to the next whole minute's worth** and then to 2 decimals using `MidpointRounding.AwayFromZero`. Show money with the server culture's currency format.
- **Prepaid** (default): the charge is quoted and fixed when the session starts. The engine prices the purchased minutes as a projected interval starting now; each extension adds a quote for the extension projected from the current planned end. Stopping early does not refund automatically, so the charge stays as quoted and is shown on stop. Moving a prepaid session to another type doesn't re-price it.
- **Open-ended**: the charge is computed at stop from the actual `SessionSegment`s. Each segment is priced at its own station's type, so moving between types is billed correctly.
- Unit tests for PricingEngine are mandatory and heavy:
  - no rules; a span crossing a happy-hour start and end; a window crossing midnight.
  - a day-of-week boundary at midnight; overlapping rules by priority; a type-specific vs an all-types rule.
  - a multiplier vs a fixed rate; zero-length and sub-minute spans; a DST transition day in a zone with DST.

### Session lifecycle (SessionService)
- `SessionService` (in `src/Cafe.Server/Sessions/`) holds all mutations. Every mutation writes a SessionEvent and returns `ServiceResult`.
  - `StartPrepaidAsync(stationId, minutes)`: the station must be enabled and idle, with no non-ended session. Minutes are 1–1440, and the quick picks in the UI are 30/60/120/180.
  - `StartOpenEndedAsync(stationId)`.
  - `PauseAsync(sessionId)` and `ResumeAsync(sessionId)`. Paused time neither counts down nor bills.
  - `ExtendAsync(sessionId, minutes)`: prepaid only. Allowed while Active, Paused or Grace. Extending during Grace returns the session to Active if remaining time is now above zero.
  - `MoveAsync(sessionId, targetStationId)`: the target must be enabled and idle. Allowed while Active or Paused.
  - `StopAsync(sessionId)`: computes the final charge, sets Ended/Stopped and returns the charge.
- Remaining time (prepaid) = PurchasedMinutes − active elapsed. Open-ended sessions show elapsed active time instead of a countdown.

### Expiry engine (SESS-08, SESS-09)
- `SessionTicker` is a `BackgroundService` that runs every second on a `PeriodicTimer` built from the injected `TimeProvider`. Each tick evaluates the non-ended sessions; the logic lives in a separately testable `SessionClock`/`ExpiryEvaluator` so tests can advance a FakeTimeProvider and call it directly.
  - **Warning:** when remaining time first crosses each configured warning point, record `WarningReached` once per threshold.
  - **Expired:** when remaining time reaches 0 on an Active prepaid session, set Status = Grace and record `Expired`.
  - **Grace elapsed:** when the grace period is used up (wall clock from expiry, not paused), end the session (Ended/Expired, charge = quoted charge). Then record `GraceElapsed` and call `IExpiryActionHandler.ExecuteAsync(station)`, recording `ExpiryActionFired`.
  - Paused sessions never expire. Open-ended sessions never expire.
- `IExpiryActionHandler` (in `Sessions/`): the default `LoggingExpiryActionHandler` only logs. Phase 3 (PC lock) and Phase 4 (TV power-off) register real handlers, so design for multiple handlers (`IEnumerable<IExpiryActionHandler>` that each check the station kind).
- Change notification: a singleton `SessionNotifier` (in-process event, `event Action<int stationId>` or a `Channel`) is raised on every session mutation and every tick state change. The dashboard subscribes so cards update immediately instead of waiting for polling. Keep the 1-second UI tick for countdown rendering.

### Desk UI
- **Dashboard cards** (`StationCard.razor`):
  - Show the session state: a countdown mm:ss (h:mm:ss over 1h) for prepaid, or elapsed time for open-ended, plus mode, current charge (quoted for prepaid, running estimate for open-ended) and the status badge.
  - Idle cards get Start prepaid (duration picker: 30/60/120/180/custom) and Start open-ended.
  - Active cards get Pause/Resume, Extend (+15/+30/+60/custom), Move (pick an idle station) and Stop, with a confirmation that shows the final charge.
- **Statuses:** InSession = Active; Paused = Paused; Expiring = Active within the warning window OR Grace. Grace shows "TIME UP" plus a grace countdown with a pulsing red style.
- **Audible alert:**
  - A tiny JS module (`wwwroot/js/alerts.js`) plays a short Web Audio beep when any station newly enters the warning window or Grace. No audio files and no CDN.
  - A header toggle mutes and unmutes sound, remembered in localStorage inside try/catch.
  - Browsers block audio until the first user gesture, so an "Enable sound" button shows until audio is unlocked.
  - Visual alert: the card border and badge pulse, and an alert strip at the top lists stations needing attention. Each alert has an Acknowledge button that silences repeat beeps for that station until its next state change.
- **Rate rules page** `/rate-rules`: CRUD table and form. Fields: name, station type (or All), day checkboxes, start/end time, kind, value, priority, enabled. Validate that Value > 0, at least one day is checked, and start ≠ end.
- **Settings page** `/settings`: grace minutes (0–30) and warning minutes (a comma list of positive ints, 1–60).
- **Session history** `/sessions`: the most recent 200 sessions, newest first, with station, mode, start, end, end reason, active duration and charge. Expanding a row shows its SessionEvents in time order (the audit trail, SESS-10). Add a station filter.
- Nav adds: Sessions, Rate Rules, Settings.

### Testing
- PricingEngine: pure unit tests (see above).
- SessionService and expiry evaluator: tests with FakeTimeProvider and temp SQLite.
  - Start, pause and resume, checking that paused time isn't counted.
  - Extend, including from Grace back to Active; move, including onto a busy target being rejected; stop and the charge.
  - Warnings recorded once per threshold; Expired → Grace → GraceElapsed with the expiry handler invoked once; paused sessions never expiring; the one-active-session-per-station rule.
- Page smoke tests through WebApplicationFactory: /rate-rules, /settings and /sessions require auth and render.
- Verify commands from the repo root: `dotnet build Cafe.sln`, `dotnet test Cafe.sln`.

## Claude's Discretion
- Exact component split, CSS, icons and modal vs inline forms.
- Whether the notifier is an event or a Channel.
- How the alert strip is laid out.

## Deferred Ideas
- Member pricing (PRIC-03) and drawing time from a customer balance → Phase 5.
- Payment recording, receipts → Phase 6. Revenue reports → Phase 7.
- PC lock/unlock and warnings on the PC screen → Phase 3. TV power-off → Phase 4.
- Partial refunds on early stop of prepaid sessions: v2 / staff handles manually.

## Environment Notes
- macOS arm64. dotnet is at `/usr/local/share/dotnet/dotnet` (`export PATH="/usr/local/share/dotnet:$PATH"`). global.json pins SDK 10.0.404, and projects target net10.0. NuGet packages: Microsoft first-party only, 10.0.x (Microsoft.Extensions.TimeProvider.Testing is OK).
- Commit on `main` as the existing global identity (dylenq), with no Co-Authored-By or Claude attribution.
- Leave untracked `.claude/launch.json`, `.planning/milestone.lock` and `.planning/state.json` alone.
