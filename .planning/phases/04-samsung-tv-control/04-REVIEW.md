---
phase: 04-samsung-tv-control
reviewed: 2026-11-20T00:00:00Z
depth: standard
files_reviewed: 28
files_reviewed_list:
  - src/Cafe.Server/Devices/ITvDriver.cs
  - src/Cafe.Server/Devices/ITvTransport.cs
  - src/Cafe.Server/Devices/SamsungTizenDriver.cs
  - src/Cafe.Server/Devices/SamsungTvTransport.cs
  - src/Cafe.Server/Devices/TvActionQueue.cs
  - src/Cafe.Server/Devices/TvControlService.cs
  - src/Cafe.Server/Devices/TvExpiryActionHandler.cs
  - src/Cafe.Server/Devices/TvModels.cs
  - src/Cafe.Server/Devices/TvMonitorService.cs
  - src/Cafe.Server/Devices/TvOptions.cs
  - src/Cafe.Server/Devices/TvPairingService.cs
  - src/Cafe.Server/Devices/TvServiceCollectionExtensions.cs
  - src/Cafe.Server/Devices/TvSessionEventSink.cs
  - src/Cafe.Server/Devices/TvSettingsService.cs
  - src/Cafe.Server/Devices/TvStateCache.cs
  - src/Cafe.Server/Devices/WakeOnLan.cs
  - src/Cafe.Server/Program.cs
  - src/Cafe.Server/Data/AgentCommandLog.cs
  - src/Cafe.Server/Dashboard/DashboardService.cs
  - src/Cafe.Server/Dashboard/DefaultStationStatusProvider.cs
  - src/Cafe.Server/Dashboard/StationCardModel.cs
  - src/Cafe.Server/Components/Pages/Home.razor
  - src/Cafe.Server/Components/Pages/Settings.razor
  - src/Cafe.Server/Components/Pages/Stations.razor
  - src/Cafe.Server/Components/Shared/StationCard.razor
  - src/Cafe.Server/Components/Shared/TvControls.razor
  - src/Cafe.Server/Components/Shared/TvPairControl.razor
  - src/Cafe.Server/Components/Shared/TvSettingsPanel.razor
findings:
  critical: 0
  warning: 6
  info: 5
  total: 11
status: issues_found
---

# Phase 4: Code Review Report

**Reviewed:** 2026-11-20
**Depth:** standard
**Files Reviewed:** 28 source files (test files and test infrastructure were not reviewed in depth)

## Summary

The core design is sound. The TLS bypass is set only on the `ClientWebSocket` (`SamsungTvTransport.cs:22`). The TV token is never put in a log message. Session operations only enqueue and never await the TV. The queue's latest-wins logic is correct, and the monitor isolates failures per TV. Disposal in `Home.razor` is correct.

No blockers were found. The issues are in the toggle-key safety margins, expiry failure handling, and stale-token hygiene.

## Warnings

### WR-01: Expiry treats "Unreachable" as a failure, retries silently, then gives up with no desk notice

**File:** `src/Cafe.Server/Devices/TvExpiryActionHandler.cs:34-37`, `TvControlService.cs:80-84`, `SamsungTizenDriver.cs:95-96`
**Issue:**
- `PowerOffAsync` returns `Unreachable` when the REST probe gets no answer. This is exactly what a TV without network standby, or one switched off at the wall, does. The settings panel itself says such models "may show Unreachable while it is off".
- The handler throws for that outcome. The evaluator then records 5 `ExpiryActionFailed` events (15 s apart) and finally writes "gave up" as `ExpiryActionFired`.
- `TvControlService` raises a desk notice only for `Failed`, not `Unreachable`. Staff therefore get no signal in the real failure case (TV on but briefly unreachable at the moment of expiry).
- The case where the TV is genuinely off is turned into noise.

**Fix:** In `TvControlService.ExecuteAsync`, raise the notice for `Unreachable` when `!on` as well. Add a final-attempt notice when the expiry gives up. For TVs that have never answered (no cached state for N polls), treat `Unreachable` at expiry as success (`AlreadyOff`-like).
```csharp
if (result.Outcome is TvActionOutcome.Failed || (!on && result.Outcome == TvActionOutcome.Unreachable))
    cache.RaiseNotice(station.Id, $"TV for {station.Name} did not respond");
```

### WR-02: Power-on can toggle the TV back off while it is still waking

**File:** `src/Cafe.Server/Devices/SamsungTizenDriver.cs:140-168`
**Issue:**
- After the WoL packets and a 4 s wait, the code re-reads the state. If the TV still reports `Standby` (a Samsung can report standby for several seconds while it boots), the code sends `KEY_POWER`.
- If the TV wakes between the read and the key, or was already mid-wake, the key toggles it off.
- On success the method also returns `PoweredOn` with `State = Standby` and never re-reads. The cache then shows the TV in Standby until the next poll, and the outcome is unverified.

**Fix:**
- Poll the state a few times (for example 3 reads, 1 s apart) before sending the key.
- Re-read after the key and return the real state.
- Return `KeySent` instead of `PoweredOn` when the re-read does not confirm `On`.

### WR-03: Power-off sends the toggle key when the state is `Unknown`, and reports success

**File:** `src/Cafe.Server/Devices/SamsungTizenDriver.cs:94-111, 215-221`
**Issue:**
- `ReadStateAsync` returns `Unknown` for a non-2xx reply, an unparseable body, or an unrecognised `PowerState` value (for example a firmware that reports something other than "on"/"standby").
- `PowerOffAsync` only short-circuits on `Standby` and `Unreachable`, so `Unknown` goes ahead and sends `KEY_POWER`. If the TV was actually in standby, this turns it **on**.
- If the TV still reads `Unknown` afterwards, `KeySent` is returned. The expiry handler counts that as success, so the TV may be left on or have been switched on, and nothing flags it.

**Fix:** For `Unknown`, either fail without sending the key, or send it at most once and make `KeySent` with an `Unknown` end state fail the expiry (so it retries and re-reads). At minimum, raise a desk notice for `KeySent` with an `Unknown` state.

### WR-04: Stale TV token survives an IP change or a change to a non-console kind

**File:** `src/Cafe.Server/Stations/StationService.cs:64, 144-151`
**Issue:**
- `TvToken` is deliberately never touched by `UpdateAsync`. When the TV IP or MAC changes, or the station is changed to a PC (the IP and MAC are nulled), the old token stays on the row.
- The token is a secret for the previous device, and the UI keeps showing "Paired" for a different TV. The first power-off then fails with "TV rejected the stored pairing".
- If the station is later turned back into a console, the old token is reused.

**Fix:** In `Prepare`, clear `entity.TvToken` when `TvIpAddress` changes or the kind is not Console.

### WR-05: Default driver timing can exceed the expiry `HandlerTimeout` (10 s)

**File:** `src/Cafe.Server/Devices/TvOptions.cs`, `src/Cafe.Server/Sessions/ExpiryEvaluator.cs:36`
**Issue:**
- The worst-case path of `PowerOffAsync` is about 13.3 s: REST 2 s + connect 3 s + channel wait 3 s + flush 0.3 s + settle 3 s + REST 2 s.
- The expiry evaluator cancels handlers after 10 s. A slow TV therefore produces spurious "timed out" failures even when the key was already sent.
- This is partly mitigated by the idempotent retry (a TV already in `Standby` returns `AlreadyOff`). It still consumes the 5-attempt budget and raises a false desk notice.

**Fix:** Cut the worst case to under about 8 s, for example by skipping the post-key settle read on expiry or lowering the timeouts. Alternatively raise `HandlerTimeout` for this handler. Add a test that asserts the worst-case sum is less than `HandlerTimeout`.

### WR-06: Wake-on-LAN broadcast assumes /24 and only reaches one interface

**File:** `src/Cafe.Server/Devices/WakeOnLan.cs:49-53`, `SamsungTvTransport.cs:75-79`, `SamsungTizenDriver.cs:176`
**Issue:**
- `SubnetBroadcast` hard-codes `a.b.c.255`, which is wrong for non-/24 networks.
- `255.255.255.255` from an unbound `UdpClient` leaves via the OS default interface only. On a multi-NIC server (for example a wired LAN plus Wi-Fi, or a VPN adapter) the packet may never reach the TV segment.
- The class comment documents the /24 assumption, but there is no override setting.

**Fix:** Add a `Tv:BroadcastAddress` option, or enumerate `NetworkInterface` unicast addresses and compute the real subnet broadcast from the mask for the interface that matches the TV IP.

## Info

### IN-01: Stale monitor write can overwrite a newer action result in the cache

**File:** `src/Cafe.Server/Devices/TvMonitorService.cs:84-85`
**Issue:** `PollOneAsync` calls `cache.Set` after `GetStateAsync` returns. The poll can start before a manual power action and finish after it, so the UI briefly shows the pre-action state until the next poll (15 s). `TvStateCache.Set` has no ordering or staleness check.
**Fix:** Keep a per-station generation or timestamp and ignore a poll result that started before the latest action write.

### IN-02: `TvToken` stored in plain text, and it travels in cleartext when `UseTls=false`

**File:** `src/Cafe.Server/Devices/TvOptions.cs:11`, `SamsungTizenDriver.cs:325-331`, `Data/CafeDbContext.cs:61`
**Issue:**
- The token is in the URL query. With `Tv:UseTls=false` (`ws://`) it crosses the LAN in the clear. The accept-any-certificate callback also means a LAN attacker can see the token even with TLS on.
- This is acceptable for a trusted cafe LAN. It is a documented trade-off, but it is not warned about in the settings UI.
**Fix:** Optionally pin the TV certificate thumbprint at pair time. At minimum, keep `UseTls` default true (it is) and note the limitation.

### IN-03: `ConnectAsync` leaks the `ClientWebSocket` on unexpected exception types

**File:** `src/Cafe.Server/Devices/SamsungTvTransport.cs:20-41`
**Issue:** Only a fixed list of exception types disposes the socket. Any other exception (for example `ObjectDisposedException`, `InvalidOperationException`) propagates with the socket undisposed. The driver then catches it, but the socket is never disposed.
**Fix:** Use a `try/finally` with a `success` flag so the socket is always disposed unless it is handed to `Socket`.

### IN-04: `OnTvNotice` / `OnTvChanged` discard tasks that can fault after the component is disposed

**File:** `src/Cafe.Server/Components/Pages/Home.razor:137-143`
**Issue:**
- `TvStateCache` raises these events from monitor and queue threads.
- If the circuit is disposed between the handler firing and `InvokeAsync` running, the discarded task faults with `ObjectDisposedException`.
- It is unobserved, so there is no crash, but it is noise. `RaiseSafe` catches only synchronous exceptions.
**Fix:** Wrap the `InvokeAsync` body in try/catch for `ObjectDisposedException`, as `RefreshNowAsync` already does.

### IN-05: Audit message for failures may expose raw exception text to the UI

**File:** `src/Cafe.Server/Devices/TvControlService.cs:73-75`
**Issue:** On a driver exception, `result = new TvActionResult(Failed, Unknown, ex.Message)`. `ManualAsync` returns `result.Message` straight to the staff UI, and it is written to `AgentCommandLog`. Current driver exceptions do not carry the token. A future driver could put the URL (with its token query) in an exception message.
**Fix:** Use a generic message (`"The TV command failed"`) for the UI and log. Keep `ex.GetType().Name` only.

---

_Reviewed: 2026-11-20_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
