---
phase: 02-session-engine-pricing
reviewed: 2026-11-20T00:00:00Z
depth: standard
files_reviewed: 52
files_reviewed_list:
  - src/Cafe.Server/Pricing/PricingEngine.cs
  - src/Cafe.Server/Pricing/CafeTimeZone.cs
  - src/Cafe.Server/Pricing/PricingConfigService.cs
  - src/Cafe.Server/Pricing/PricingModels.cs
  - src/Cafe.Server/Pricing/RateRuleInput.cs
  - src/Cafe.Server/Pricing/RateRuleService.cs
  - src/Cafe.Server/Sessions/ExpiryEvaluator.cs
  - src/Cafe.Server/Sessions/IExpiryActionHandler.cs
  - src/Cafe.Server/Sessions/LoggingExpiryActionHandler.cs
  - src/Cafe.Server/Sessions/SessionCharges.cs
  - src/Cafe.Server/Sessions/SessionClock.cs
  - src/Cafe.Server/Sessions/SessionHistoryService.cs
  - src/Cafe.Server/Sessions/SessionNotifier.cs
  - src/Cafe.Server/Sessions/SessionService.cs
  - src/Cafe.Server/Sessions/SessionSettings.cs
  - src/Cafe.Server/Sessions/SessionSettingsInput.cs
  - src/Cafe.Server/Sessions/SessionSettingsService.cs
  - src/Cafe.Server/Sessions/SessionStatusMapper.cs
  - src/Cafe.Server/Sessions/SessionTicker.cs
  - src/Cafe.Server/Dashboard/AlertTracker.cs
  - src/Cafe.Server/Dashboard/DashboardService.cs
  - src/Cafe.Server/Dashboard/IStationStatusProvider.cs
  - src/Cafe.Server/Dashboard/SessionAlerts.cs
  - src/Cafe.Server/Dashboard/SessionAwareStatusProvider.cs
  - src/Cafe.Server/Dashboard/StationCardModel.cs
  - src/Cafe.Server/Data/CafeDbContext.cs
  - src/Cafe.Server/Data/RateRule.cs
  - src/Cafe.Server/Data/Session.cs
  - src/Cafe.Server/Data/SessionEnums.cs
  - src/Cafe.Server/Data/SessionEvent.cs
  - src/Cafe.Server/Data/SessionSegment.cs
  - src/Cafe.Server/Components/Pages/Home.razor
  - src/Cafe.Server/Components/Pages/RateRules.razor
  - src/Cafe.Server/Components/Pages/Sessions.razor
  - src/Cafe.Server/Components/Pages/Settings.razor
  - src/Cafe.Server/Components/Pages/Login.razor
  - src/Cafe.Server/Components/Shared/AlertStrip.razor
  - src/Cafe.Server/Components/Shared/SoundToggle.razor
  - src/Cafe.Server/Components/Shared/StationCard.razor
  - src/Cafe.Server/Components/Shared/StatusBadge.razor
  - src/Cafe.Server/Components/Layout/MainLayout.razor
  - src/Cafe.Server/Components/Layout/NavMenu.razor
  - src/Cafe.Server/Components/App.razor
  - src/Cafe.Server/Program.cs
  - src/Cafe.Server/Stations/ServiceResult.cs
  - src/Cafe.Server/Stations/StationTypeService.cs
  - src/Cafe.Server/wwwroot/js/alerts.js
  - src/Cafe.Server/wwwroot/app.css
  - src/Cafe.Server/Cafe.Server.csproj
  - tests/Cafe.Tests (all Phase 2 test files, skimmed only)
findings:
  critical: 1
  warning: 5
  info: 5
  total: 11
status: issues_found
---

# Phase 2: Code Review Report

**Reviewed:** 2026-11-20
**Depth:** standard
**Files Reviewed:** 52

## Summary

The pricing engine is sound. I traced DST cuts, midnight-crossing rules, whole-minute padding and rounding, and I found no billing defects. The Version concurrency token and the filtered unique index are wired correctly, and the stale/busy paths are handled. Authorization is covered: `_Imports.razor` applies `[Authorize]` globally, and Login/Setup opt out.

The main defect is in the expiry path. The "exactly once" handler guarantee is really "at most once, and lost on crash". That breaks the project's core value (the station reliably stops being usable). There are also Blazor card-state and ticker-resilience issues.

## Critical Issues

### CR-01: Expiry action is lost if the process stops between the Ended commit and handler execution

**File:** `src/Cafe.Server/Sessions/ExpiryEvaluator.cs:152-185` (commit at line 141, `FireHandlersAsync` at line 148)
**Issue:** The session is committed as `Ended` first, and the handlers run afterwards. `EvaluateAsync` only selects Active/Grace sessions, so an Ended session is never revisited. The handlers are skipped for good if:
- the service restarts, crashes or loses power between the commit and the handler call, or
- `OperationCanceledException` is rethrown at line 166 during shutdown, or
- a handler throws.

A handler failure is only recorded as an event and never retried. In the later phases, a PC that doesn't lock or a TV that doesn't power off means the customer keeps free use of the station after the time has run out. The class doc and the "restart recovery" claim only hold for sessions that are still Grace at restart.

**Fix:** Make the handler execution durable and idempotent.
- Do not treat Ended as the end of the work. Select sessions where `Status == Ended && EndReason == Expired` and no `ExpiryActionFired` event exists, and run the handlers for those on each tick and at startup.
- Alternatively, add a nullable `ExpiryActionFiredAtUtc` column. Set it only after the handlers succeed, and let failed handlers retry with backoff. Handlers must be idempotent.
- Do not rethrow cancellation before the marker is written.

```csharp
// in EvaluateAsync, additionally:
var pending = await db.Sessions.Where(s => s.Status == SessionStatus.Ended
    && s.EndReason == SessionEndReason.Expired
    && !s.Events.Any(e => e.Type == SessionEventType.ExpiryActionFired)).Select(s => s.Id).ToListAsync(ct);
```

## Warnings

### WR-01: A slow or hung expiry handler stalls expiry for every other session

**File:** `src/Cafe.Server/Sessions/ExpiryEvaluator.cs:46-62, 152-170`
**Issue:** Sessions are processed sequentially, and handlers are awaited inline with no timeout. The Phase 3/4 handlers will make network calls to PCs and TVs. One unreachable device would delay every other session's Grace->Ended transition by the full handler timeout, once per affected session. The ticker also never completes its pass in that time.
**Fix:** Run each handler with a linked `CancellationTokenSource` timeout (for example 10 s). Alternatively, dispatch the handler work to a bounded queue or per-station task outside the evaluation loop, and record the outcome there.

### WR-02: Stop-confirm state leaks onto a different session on the same station card

**File:** `src/Cafe.Server/Components/Shared/StationCard.razor:36-47, 141, 224-251`
**Issue:** `StationCard` is keyed by `StationId`, so the instance survives session changes. `_confirmQuote` is never cleared when `Model.Session` becomes null or changes `SessionId`. The confirm panel is only dropped on a successful or failed stop, or on Cancel.

Scenario:
1. Staff clicks Stop, which opens the panel.
2. The session ends meanwhile (grace expiry or another tab), and a new session is later started on that station.
3. The new session's card renders the old "Final charge" panel, and "Confirm stop" is bound to the new `session.SessionId`. One click stops the new session at a charge the staff never saw.

**Fix:** Track the session the quote belongs to and discard it on mismatch.
```csharp
private int? _confirmSessionId;
// set together with _confirmQuote in BeginStopAsync; render the panel only when
// _confirmQuote is not null && _confirmSessionId == session.SessionId
// and reset both in OnParametersSet when Model.Session?.SessionId != _confirmSessionId.
```

### WR-03: The "Final charge" shown at stop confirmation is not locked in

**File:** `src/Cafe.Server/Components/Shared/StationCard.razor:203-206, 230`, `src/Cafe.Server/Sessions/SessionService.cs:195-216`
**Issue:** `GetStopQuoteAsync` prices an open-ended session at quote time, and `StopAsync` re-prices at the later confirm time. If staff dwell on the confirm panel across a minute boundary or a rate-rule boundary, the amount billed differs from the "Final charge" label shown. The notice after the stop shows the new number, so the customer is quoted one price and charged another.
**Fix:** Pass the quote and its timestamp to `StopAsync` and bill at that instant, if it is recent. Or return the actual charge and re-display it in the panel when it differs from the quoted figure (require a second confirm).

### WR-04: Extend can revive a session whose grace has already elapsed but has not been ticked yet

**File:** `src/Cafe.Server/Sessions/SessionService.cs:133-139`
**Issue:** In the Grace branch, the code never checks `GraceStartedAtUtc + grace` against `ctx.Now`. Between the real grace end and the next ticker pass (up to about 1 s, longer if a handler is slow, see WR-01), an Extend flips the session back to Active. The session is billed and runs although the policy says it was over. If the evaluator wins the race instead, the user sees "already ended". The outcome depends only on timing.

Pause has a related edge. An Active session whose remaining time is already at or below zero but not yet ticked can be paused. This closes the segment past the purchased time, so `active` exceeds `purchased`.
**Fix:** In `ExtendAsync` and `PauseAsync`, load the settings and reject, or resolve through the evaluator's logic, when `ctx.Now >= GraceStartedAtUtc + GracePeriod`. For Pause, also reject when remaining is at or below zero.

### WR-05: Overlapping dashboard refreshes can apply stale data over newer data

**File:** `src/Cafe.Server/Components/Pages/Home.razor:157-175, 207-215`
**Issue:** `RefreshNowAsync` (notifier-triggered) and `RefreshLoopAsync` (5 s poll) both fetch outside `InvokeAsync` and then assign `_cards`. Responses can arrive out of order, so a fetch started before a mutation can overwrite the post-mutation result. The dashboard then shows a pre-stop or pre-extend state until the next poll (up to 5 s). The notifier burst also fires one DB read per subscribed circuit for every mutation.
**Fix:** Take a monotonically increasing sequence number before each fetch. Inside `InvokeAsync`, apply the result only if it is newer than the last applied one, or serialise refreshes with a `SemaphoreSlim(1,1)` and coalesce requests.

## Info

### IN-01: `cafeAlerts.unlock` returns the state before `resume()` has resolved

**File:** `src/Cafe.Server/wwwroot/js/alerts.js:50-62`, `src/Cafe.Server/Components/Shared/SoundToggle.razor:44-49`
**Issue:** `unlock` calls `c.resume()` asynchronously and immediately returns `c.state === 'running'`. Where the context is created suspended (Safari, or a context created before the gesture), the first click returns false, so the button stays "Enable sound" and needs a second click. Staff will wrongly believe sound is on.
**Fix:** Return the promise (`return c.resume().then(() => c.state === 'running').catch(() => false)`) and `await` it in the component. The module itself is safe: there is no DOM injection, storage access is guarded, and the arguments are fixed strings.

### IN-02: Closed-segment seconds are truncated per segment

**File:** `src/Cafe.Server/Sessions/ExpiryEvaluator.cs:79`, `SessionService.cs:339`, `DashboardService.cs:68`, `SessionHistoryService.cs:46`
**Issue:** `(long)(End - Start).TotalSeconds` is computed per segment and then summed. Each pause, resume or move loses up to 1 s of countdown, and the same truncation is repeated in four places. The engine also floors both endpoints independently via `ToUnixTimeSeconds`. The effect is bounded (at most about 1 s per segment) but unintended, and it favours the customer.
**Fix:** Move the sum into one `SessionClock.ClosedSeconds(IEnumerable<SessionSegment>)` helper and sum the ticks before converting to seconds.

### IN-03: Open-ended charges and history are re-priced with the current rate rules

**File:** `src/Cafe.Server/Sessions/SessionCharges.cs:41`, `src/Cafe.Server/Pricing/RateRuleService.cs`
**Issue:** An open-ended session is priced at stop time with whichever rules are enabled then. Editing or toggling a rule during a running session retroactively changes the price of the time already used. Prepaid quotes are frozen, so the two modes behave differently. This is probably acceptable, but it is undocumented and unflagged in the UI.
**Fix:** Document the behaviour, or snapshot the rules onto the segments when a rule changes mid-session.

### IN-04: Currency formatting depends on the server's culture

**File:** `StationCard.razor:48,57,234`, `Sessions.razor:63`, `RateRules.razor:230`
**Issue:** `ToString("C")` uses the server thread culture, but the event log text uses `0.00` with no symbol. A server with an en-US locale shows "$" in a non-US cafe. This is also inconsistent across screens.
**Fix:** Add a configured `Cafe:Currency` or `CultureInfo`, and format through one shared helper.

### IN-05: Over-broad catch hides non-race failures when saving settings; handler error text is stored unbounded

**File:** `src/Cafe.Server/Sessions/SessionSettingsService.cs:65-69`, `ExpiryEvaluator.cs:175`
**Issue:** The settings save treats any `DbUpdateException` as an insert race, so a locked DB or a constraint failure is silently reported as "changed at the same time", with no logging. In `ExpiryEvaluator`, `ex.Message` is appended to `SessionEvent.Details`, which is configured `HasMaxLength(300)`. SQLite will not enforce that, so the model and the data can disagree.
**Fix:** Filter with `InputValidation.IsUniqueViolation(ex)` and log the rest. Truncate failure details to the configured length.

---

_Reviewed: 2026-11-20_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
