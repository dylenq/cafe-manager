---
phase: 02-session-engine-pricing
fixed_at: 2026-11-20T00:00:00Z
review_path: /Users/dylenquyn/Cafe/.planning/phases/02-session-engine-pricing/02-REVIEW.md
iteration: 1
findings_in_scope: 8
fixed: 8
skipped: 0
status: all_fixed
---

# Phase 2: Code Review Fix Report

**Fixed at:** 2026-11-20
**Source review:** /Users/dylenquyn/Cafe/.planning/phases/02-session-engine-pricing/02-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 8 (CR-01, WR-01..WR-05, plus IN-01 and IN-04 by request)
- Fixed: 8
- Skipped: 0

**Verification:** ran in the main checkout (not a worktree), as instructed by the coordinator (commits on main, another agent writing untracked files in the same tree). `dotnet build Cafe.sln` clean (0 warnings, 0 errors) and `dotnet test Cafe.sln` passes (320 tests). CR-01, WR-01, WR-02/03 and WR-04 are logic changes, so they are flagged "fixed: requires human verification".

## Fixed Issues

### CR-01: Expiry action is lost if the process stops between the Ended commit and handler execution

**Status:** fixed: requires human verification
**Files modified:** `src/Cafe.Server/Sessions/ExpiryEvaluator.cs`, `src/Cafe.Server/Sessions/IExpiryActionHandler.cs`, `src/Cafe.Server/Data/SessionEnums.cs`, `tests/Cafe.Tests/ExpiryEvaluatorTests.cs`
**Commit:** 7f613b9
**Applied fix:** Handlers no longer run inline after the Ended commit. Every evaluator pass (including the startup pass) now also selects sessions with `Status == Ended`, `EndReason == Expired`, `EndedAtUtc` within the last 24 h and no `ExpiryActionFired` event, and runs the handlers for them. `ExpiryActionFired` is written only after the handlers ran successfully. Failures are recorded as a new `ExpiryActionFailed` event (value 10; the column is stored as int, so no migration was needed), with Details truncated to 300 characters (also covers IN-05's truncation part). Retries wait at least 15 s and stop after 5 attempts, counted from `ExpiryActionFailed` events; after the fifth failure a final `ExpiryActionFired` "gave up after 5 failed attempts" marker stops further retries. Cancellation at shutdown records nothing, so the next start retries. The idempotency contract is documented on `IExpiryActionHandler`.
**Decisions:** event-count tracking over a new column (no migration, audit trail visible in the history). Constants: `ActionLookback` 24 h, `MaxActionAttempts` 5, `ActionRetryDelay` 15 s.

### WR-01: A slow or hung expiry handler stalls expiry for every other session

**Status:** fixed: requires human verification
**Files modified:** `src/Cafe.Server/Sessions/ExpiryEvaluator.cs`, `tests/Cafe.Tests/ExpiryEvaluatorTests.cs`
**Commit:** 869ae47
**Applied fix:** Each handler call has a 10 s timeout (`HandlerTimeout`, internal and settable for tests) using a linked `CancellationTokenSource` plus `Task.WaitAsync`, so a handler that ignores its token is still abandoned. A timeout counts as a failed attempt. Pending sessions now run concurrently (`Task.WhenAll`). The test `RecordingHandler` was made thread-safe.

### WR-02 and WR-03: Stop-confirm leaks onto another session / charge not locked in

**Status:** fixed: requires human verification
**Files modified:** `src/Cafe.Server/Sessions/SessionService.cs`, `src/Cafe.Server/Components/Shared/StationCard.razor`, `tests/Cafe.Tests/SessionLifecycleTests.cs`
**Commit:** 11f2708
**Applied fix:** New `QuoteStopAsync` returns a `StopQuote(SessionId, Version, Amount)`; `GetStopQuoteAsync` is kept as a wrapper. `StopAsync(sessionId, expectedVersion = null, expectedAmount = null, ct)` fails with "Session changed — review the new charge" if the version differs or the charge computed at confirm time differs from the quoted amount (this also covers open-ended charges that grow with time). The card stores the session id, version and amount with the quote, only renders the panel for that session id, clears it in `OnParametersSet` when the session changes, and on the "changed" error re-quotes and shows the new charge for another confirmation.
**Decisions:** any version bump (including a warning event written by the ticker) makes the confirm re-ask once; this is the safe direction. No component test, as the project has no bUnit; the service behaviour is covered by three new tests.

### WR-04: Extend can revive a session whose grace already elapsed

**Status:** fixed: requires human verification
**Files modified:** `src/Cafe.Server/Sessions/ExpiryEvaluator.cs`, `src/Cafe.Server/Sessions/SessionService.cs`, `tests/Cafe.Tests/SessionExtendMoveTests.cs`, `tests/Cafe.Tests/ExpiryEvaluatorTests.cs`
**Commit:** c7b9de9
**Applied fix:** `ExpiryEvaluator.ResolveAsync(sessionId)` settles one session at the current instant (using the stored true expiry instants). `SessionService` (now taking `ExpiryEvaluator`) calls it before Extend, Pause and Move. If that ended the session, the call fails with "The time and grace period are over, so the session has ended. Start a new session." An unticked just-expired session becomes Grace first (so Pause is refused and Extend revives from the true expiry). Pause additionally refuses when remaining time is at or below zero, so a segment is never closed past the purchased time. Handlers for a session ended this way run on the next pass.

### WR-05: Overlapping dashboard refreshes can apply stale data

**Status:** fixed
**Files modified:** `src/Cafe.Server/Components/Pages/Home.razor`, `src/Cafe.Server/Dashboard/LatestWinsGate.cs` (new), `tests/Cafe.Tests/LatestWinsGateTests.cs` (new)
**Commit:** ea2b52c
**Applied fix:** Both refresh paths go through one `FetchAndApplyAsync`: a ticket is taken before the fetch and the result is applied inside `InvokeAsync` only if no newer ticket has been applied. The ordering logic is a small unit-tested class.

### IN-01: `cafeAlerts.unlock` returns the state before `resume()` resolved

**Status:** fixed
**Files modified:** `src/Cafe.Server/wwwroot/js/alerts.js`
**Commit:** bc12365
**Applied fix:** `unlock` now returns a promise resolving to whether the context is running after `resume()`. `SoundToggle.razor` already awaits `Js.InvokeAsync<bool>`, which awaits a returned promise, so no component change was needed. Checked with `node -c`; no automated test (no JS test harness).

### IN-04: Currency formatting depends on the server's culture

**Status:** fixed
**Files modified:** `src/Cafe.Server/Pricing/CafeCulture.cs` (new), `src/Cafe.Server/Program.cs`, `src/Cafe.Server/appsettings.json`, `README.md`, `tests/Cafe.Tests/CafeCultureTests.cs` (new)
**Commit:** 872e98a
**Applied fix:** `Cafe:Culture` (default empty) is resolved with `predefinedOnly: true` and applied at startup as `DefaultThreadCurrentCulture` and `DefaultThreadCurrentUICulture`. Empty or unknown names log a warning and keep the server culture. Documented in the README configuration table and `appsettings.json`.

## Notes

- One run of `SessionTickerTests.Start_evaluates_once_immediately_and_recovers_a_session_left_in_grace` failed once (real-time test) and passed in 5 later full runs; it looks timing-flaky and was not investigated further.
- Not in scope and left untouched: IN-02, IN-03, IN-05 (only its truncation half was fixed as part of CR-01).

---

_Fixed: 2026-11-20_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
