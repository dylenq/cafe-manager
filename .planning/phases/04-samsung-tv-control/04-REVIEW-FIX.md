---
phase: 04-samsung-tv-control
fixed_at: 2026-11-20T00:00:00Z
review_path: .planning/phases/04-samsung-tv-control/04-REVIEW.md
iteration: 1
findings_in_scope: 10
fixed: 10
skipped: 0
status: all_fixed
---

# Phase 4: Code Review Fix Report

**Fixed at:** 2026-11-20
**Source review:** .planning/phases/04-samsung-tv-control/04-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 10 (WR-01..WR-06, IN-01, IN-03, IN-04, IN-05; IN-02 was out of scope by request)
- Fixed: 10
- Skipped: 0

**Verification:** ran in the isolated worktree (branch `worktree-agent-a256e6fcba24c56f4`), not the main checkout. `dotnet build Cafe.sln` is clean (0 warnings, 0 errors) and `dotnet test Cafe.sln` passes (608 tests, baseline was 586). WR-01, WR-02, WR-03, WR-04, WR-05, WR-06 and IN-01 are logic changes, so they are flagged "fixed: requires human verification". The real Samsung TV behaviour (boot timing, REST states) cannot be verified here; only the fake transport and the fake TV were used.

## Fixed Issues

### WR-01: Expiry treats "Unreachable" as a failure

**Status:** fixed: requires human verification
**Files modified:** `src/Cafe.Server/Devices/TvExpiryActionHandler.cs`, `src/Cafe.Server/Devices/TvControlService.cs`, `tests/Cafe.Tests/TvDriverTests.cs`, `tests/Cafe.Tests/TvSessionSinkTests.cs`
**Commit:** 399e9b8
**Applied fix:** At expiry an Unreachable outcome now counts as "already off" (the handler no longer throws). `TvControlService` raises one desk notice, "TV for X unreachable at expiry - check it's off", for source "Expiry" only (the cache's existing 60 s per-station window rate-limits it). Failed stays a retryable failure, with the existing "did not respond" notice. Two existing tests that asserted the old throw were updated.
**Decision:** manual power-off with an Unreachable TV is unchanged (still reported to staff as not successful). The review's optional "give up" notice after five attempts was not added.

### WR-03: Power-off sent the toggle key on an Unknown state

**Status:** fixed: requires human verification
**Files modified:** `src/Cafe.Server/Devices/SamsungTizenDriver.cs`, `tests/Cafe.Tests/TvDriverTests.cs`
**Commit:** 4f0c122
**Applied fix:** An Unknown state is re-read once. If it is still Unknown the driver returns Failed ("state could not be read, so the power key was not sent") without opening the channel, which the expiry handler treats as retryable. If the re-read gives Standby, Unreachable or On the normal paths apply. The old test "Unknown_state_sends_the_key" was replaced by three tests covering these branches.

### WR-02: Power-on could toggle the TV back off while it was waking

**Status:** fixed: requires human verification
**Files modified:** `src/Cafe.Server/Devices/SamsungTizenDriver.cs`, `src/Cafe.Server/Devices/TvOptions.cs`, `tests/Cafe.Tests/TvDriverTests.cs`
**Commit:** 05dc77f
**Applied fix:** After the Wake-on-LAN packets the driver watches the TV for `Tv:WakeWaitMilliseconds` (default raised from 4000 to 8000), reading the state every `Tv:WakePollMilliseconds` (new, default 1000) and stopping at the first On. KEY_POWER is sent only if the TV is paired, every read in the window was Standby, and a fresh re-read immediately before the key is still Standby. After the key it waits the settle time and re-reads: On gives `PoweredOn`; anything else gives `KeySent` with the fresh state (never a claimed `PoweredOn`). A TV that left standby, blipped Unreachable, or woke between the window and the key gets no key (`WakeSent`/`PoweredOn`). A station with no MAC (no WoL) uses the single initial Standby read.
**Decision:** the loop is deadline based, so the window cannot exceed about 8 s plus one read timeout, keeping the whole power-on inside the 30 s queue action timeout.

### WR-04: Stale TV token survived an IP change or a change to a non-console kind

**Status:** fixed: requires human verification
**Files modified:** `src/Cafe.Server/Stations/StationService.cs`, `tests/Cafe.Tests/TvReviewFixTests.cs` (new), `tests/Cafe.Tests/StationServiceTests.cs`
**Commit:** 1d3c62b
**Applied fix:** `UpdateAsync` clears `TvToken` when the (cleaned) `TvIpAddress` changes or the kind is no longer Console. Edits to the name or MAC keep the token. The existing test `Update_switching_kind_clears_other_fields_and_keeps_tv_token` encoded the old behaviour and was changed to expect a cleared token. Only `UpdateAsync` was touched, not `Prepare`, to keep the diff minimal for the Phase 5 merge.

### WR-05: Default driver timing could exceed the 10 s expiry handler timeout

**Status:** fixed: requires human verification
**Files modified:** `src/Cafe.Server/Devices/TvExpiryActionHandler.cs`, `src/Cafe.Server/Devices/TvOptions.cs`, `tests/Cafe.Tests/TvDriverTests.cs`, `tests/Cafe.Tests/TvReviewFixTests.cs`
**Commit:** 5583913
**Applied fix:** New option `Tv:ExpiryActionTimeoutSeconds` (default 8). The handler runs the power-off under a linked token with that budget; when it runs out (and the caller was not cancelled) it throws a retryable `InvalidOperationException` ("did not finish powering off within 8 s; it will be retried") instead of letting the evaluator time out at 10 s. The retry reads Standby and ends as `AlreadyOff`, so the key is never sent twice. Caller cancellation (shutdown) still propagates as cancellation. Tests cover the default being below 10 s, a hung driver failing in under 5 s, and cancellation passing through. The handler now takes `IOptions<TvOptions>` (DI resolves it automatically).
**Decision:** the driver's own timeouts were left alone (the worst case is still about 13 s), the budget bounds it. A timed-out attempt may still raise the existing "did not respond" notice.

### WR-06: Wake-on-LAN broadcast assumed /24 and reached only one interface

**Status:** fixed: requires human verification
**Files modified:** `src/Cafe.Server/Devices/WakeOnLan.cs`, `src/Cafe.Server/Devices/ITvTransport.cs`, `src/Cafe.Server/Devices/SamsungTvTransport.cs`, `src/Cafe.Server/Devices/SamsungTizenDriver.cs`, `tests/Cafe.Tests/Infrastructure/FakeTvTransport.cs`, `tests/Cafe.Tests/TvDriverTests.cs`
**Commit:** 171792c
**Applied fix:** `ITvTransport.GetLocalInterfaces()` lists the up, non-loopback IPv4 interfaces with their masks (empty if enumeration fails). `WakeOnLan.BroadcastTargets` computes the directed broadcast of every interface whose subnet contains the TV and returns only those; with no match it falls back to the /24 broadcast plus 255.255.255.255 (the old behaviour). The fake transport returns no interfaces by default, so existing tests still exercise the fallback.
**Decision:** when an interface matches, 255.255.255.255 is not sent, as the guidance specified.

### IN-01: Stale monitor write could overwrite a newer action result

**Status:** fixed: requires human verification
**Files modified:** `src/Cafe.Server/Devices/TvStateCache.cs`, `src/Cafe.Server/Devices/TvMonitorService.cs`, `tests/Cafe.Tests/TvMonitorStatusTests.cs`
**Commit:** 7170214
**Applied fix:** The cache has a monotonic sequence. `Set` (power actions, explicit test) stamps the station. The monitor takes `BeginPoll()` before reading the TV and stores with `SetFromPoll(id, state, marker)`, which drops the result when an action stamped the station after the marker. A unit test and a monitor test (slow poll versus a power action) cover it.

### IN-03: ConnectAsync leaked the ClientWebSocket on unexpected exceptions

**Status:** fixed
**Files modified:** `src/Cafe.Server/Devices/SamsungTvTransport.cs`
**Commit:** 07211d6
**Applied fix:** The socket is disposed in a `finally` unless it was handed to the caller, so every exception type is covered. No unit test: the path needs a real `ClientWebSocket` failing in an unlisted way.

### IN-05: Raw exception text could reach the UI and the audit log

**Status:** fixed
**Files modified:** `src/Cafe.Server/Devices/TvControlService.cs`, `tests/Cafe.Tests/TvReviewFixTests.cs`
**Commit:** 248a58c
**Applied fix:** An unexpected driver exception now yields the fixed message `TvControlService.UnexpectedFailureMessage` ("The TV command failed. Check the TV and try again") for the UI and the AgentCommandLog row. Type and message go to the server log only. A test throws an exception whose text contains a token URL and checks neither the result nor the audit row contains it.

### IN-04: Home.razor TV event handlers could fault after disposal

**Status:** fixed
**Files modified:** `src/Cafe.Server/Components/Pages/Home.razor`
**Commit:** a269fab
**Applied fix:** `OnTvChanged` and `OnTvNotice` return early once the page's cancellation source is cancelled. The notice path moved into `ShowTvNoticeAsync`, which wraps `InvokeAsync` and swallows `OperationCanceledException`/`ObjectDisposedException` (other exceptions are logged at Debug). `OnTvChanged` already went through `RefreshNowAsync`, which catches. No component test (the project has no bUnit).

## Skipped Issues

None. IN-02 (token in plain text / cleartext with `UseTls=false`) was excluded from scope by request.

## Process note

The WR-01 commit initially left `TvSessionSinkTests.ExpiryHandler_logs_updates_the_cache_and_throws_on_failure` failing; I found it while running the WR-02 tests and folded the test fix into the WR-01 commit with an autosquash rebase (local, unpushed), so every commit on the branch builds and passes.

---

_Fixed: 2026-11-20_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
