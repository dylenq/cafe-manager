---
phase: 04-samsung-tv-control
verified: 2026-11-20T00:00:00Z
status: human_needed
score: 4/4 must-haves verified
covered_files:
  - src/Cafe.Server/Devices/SamsungTizenDriver.cs
  - src/Cafe.Server/Devices/TvControlService.cs
  - src/Cafe.Server/Devices/TvExpiryActionHandler.cs
covered_digest: "v2:sha256:87c70127080bb6865668f8ece58e08be2ee1baa9f777a5871257e016112a7ee7"
behavior_unverified: 0
human_verification:
  - test: "Pair a real Samsung Tizen TV from /stations (Pair TV), accepting the on-screen prompt"
    expected: "Station shows Paired; token stored; a model that returns no token stores the '-' marker and still works"
    why_human: "Needs real TV hardware and its prompt"
  - test: "Run a console session to expiry plus grace on a real TV"
    expected: "TV goes to standby once (KEY_POWER toggles, so it must not switch back on)"
    why_human: "Real TV REST PowerState and key behaviour differ by model"
  - test: "Start a console session while the TV is in standby (network standby / Power On with Mobile enabled)"
    expected: "TV wakes via Wake-on-LAN and is not toggled back off during boot (WR-02 window, default 8 s)"
    why_human: "Boot timing and WoL reach depend on the real LAN and TV"
  - test: "Dashboard card TV On / TV Off buttons and indicator on a real TV; unplug the TV"
    expected: "Indicator moves On / Standby / Unreachable within the poll interval; idle console with unreachable TV shows Offline"
    why_human: "Reachability and power-state semantics need real hardware"
  - test: "Multi-NIC / non-/24 server network"
    expected: "WoL reaches the TV (WR-06 subnet broadcast per interface)"
    why_human: "Depends on the cafe's real network layout"
---

# Phase 4: Samsung TV Control Verification Report

**Phase Goal:** Console stations stop being usable when paid time runs out and start up when a session begins (console Samsung TVs power off on expiry, wake on session start, with pairing and manual control from the app)
**Verified:** 2026-11-20
**Status:** human_needed
**Re-verification:** No, initial verification
**Evidence base:** code and tests read; the full `dotnet test Cafe.sln` run (1054/1054 passed) was supplied by the caller, no build was run here.

## Observable Truths

| # | Truth (ROADMAP SC) | Status | Evidence |
|---|---|---|---|
| 1 | Staff can pair a TV from Settings/Stations by accepting the prompt; token stored for that station | VERIFIED | `SamsungTizenDriver.PairAsync` waits for `ms.channel.connect`, reads `data.token`, handles unauthorized/timeout. `TvPairingService.PairAsync` writes `station.TvToken`. `Stations.razor:144` mounts `TvPairControl`. Tests: `TvTracerTests.Pairing_through_the_real_transport_stores_the_token`, `A_tv_that_refuses_pairing_leaves_the_token_empty`, `TvUiTests.Stations_page_shows_paired_and_never_the_token`. Cert bypass confined to `SamsungTvTransport` (line 27). |
| 2 | On console expiry and end of grace the TV powers off automatically | VERIFIED | `TvExpiryActionHandler` registered as `IExpiryActionHandler` (`TvServiceCollectionExtensions`), enumerated by `ExpiryEvaluator` (`IEnumerable<IExpiryActionHandler>`). It guards against a newer session, bounds the call by `ExpiryActionTimeoutSeconds` (8 s < 10 s handler timeout), treats Unreachable as already off, retries on Failed. Test: `TvTracerTests.An_expired_console_session_powers_its_tv_off_exactly_once`. |
| 3 | Starting a console session wakes the TV with Wake-on-LAN | VERIFIED | `TvSessionEventSink` (registered `ISessionEventSink`) enqueues PowerOn on Started/Resumed and on Moved for the new station, PowerOff on Stop (setting-gated) and for the old station on move; never awaited inline (`TvActionQueue`). `SamsungTizenDriver.PowerOnAsync` sends magic packets (`WakeOnLan.BroadcastTargets` from real interfaces), watches wake window, presses KEY_POWER only if standby throughout. Tests: `TvSessionSinkTests`, `TvDriverTests`. |
| 4 | Dashboard shows each TV's reachability/power state; staff can power on/off manually | VERIFIED | `TvMonitorService` polls enabled console TVs into `TvStateCache`; `DashboardService` passes `TvState` to `StationCard.razor:35` -> `TvControls.razor` (indicator, TV On, TV Off with confirm when a session runs). `DefaultStationStatusProvider` makes an idle console with an unreachable TV Offline. `TvControlService` is the single audited path (AgentCommandLog). Tests: `TvUiTests.Console_card_shows_the_cached_tv_state_and_both_buttons`, `Manual_power_off_*`, `TvMonitorStatusTests`. |

**Score:** 4/4 truths verified by code and tests. Real-TV behaviour is routed to human verification.

## 04-REVIEW-FIX.md fixes (10/10 present in code and git history)

| Finding | Evidence |
|---|---|
| WR-01 (399e9b8) | `TvExpiryActionHandler` only throws on Failed; `TvControlService` raises "unreachable at expiry" notice for source "Expiry" |
| WR-02 (05dc77f) | `WatchWakeAsync` returns `standbyThroughout`; key only sent when true and a recheck still reads Standby |
| WR-03 (4f0c122) | `PowerOffAsync` re-reads Unknown once, then returns Failed without sending the key |
| WR-04 (1d3c62b) | `StationService` lines 64-75 clear `TvToken` when TV IP changes or kind is not Console |
| WR-05 (5583913) | `TvOptions.ExpiryActionTimeoutSeconds = 8`; handler links a CTS to it |
| WR-06 (171792c) | `WakeOnLan.BroadcastTargets(tv, transport.GetLocalInterfaces())` used in `SendMagicPacketsAsync` |
| IN-01 (7170214) | `TvStateCache.BeginPoll` / `SetFromPoll(marker)` used by `TvMonitorService.PollOneAsync` |
| IN-03 (07211d6) | `SamsungTvTransport.ConnectAsync` disposal fix, commit present |
| IN-04 (a269fab) | `Home.razor` handlers catch `OperationCanceledException or ObjectDisposedException` |
| IN-05 (248a58c) | `TvControlService.UnexpectedFailureMessage` used instead of exception text; details only in log |

## Requirements Coverage

| Req | Status | Evidence |
|---|---|---|
| TVCT-01 | SATISFIED | Truth 1 |
| TVCT-02 | SATISFIED | Truth 2 |
| TVCT-03 | SATISFIED | Truth 3 |
| TVCT-04 | SATISFIED | Truth 4 |

No orphaned requirements (REQUIREMENTS.md maps only TVCT-01..04 to Phase 4).

## Anti-Patterns

No TBD/FIXME/XXX in Phase 4 files. No stubs found; data flows from real driver to cache to dashboard.

## Notes

- Minor: the review's optional "give up after five attempts" desk notice was deliberately not added (documented in WR-01); not a gap.
- Context says Settings page hosts the PowerOffOnStop toggle and help (`TvSettingsPanel` in `Settings.razor:63`, tests present).

## Human Verification Required

See frontmatter: five hardware-dependent checks (pairing, expiry power-off, WoL wake, live dashboard state, network layout).

---
_Verified: 2026-11-20_
_Verifier: Claude (gsd-verifier)_
