---
phase: 04-samsung-tv-control
plan: 01
subsystem: devices
tags: [samsung-tizen, websocket, wake-on-lan, expiry-handler, fake-tv]
requires:
  - phase: 02-session-engine-pricing
    provides: IExpiryActionHandler seam, ExpiryEvaluator retry semantics
provides:
  - ITvDriver / ITvTransport with SamsungTizenDriver and the real SamsungTvTransport
  - TvPairingService (stores Station.TvToken)
  - TvExpiryActionHandler registered as an IExpiryActionHandler
  - FakeSamsungTv in-process test TV and FakeTvTransport scripted transport
affects: [04-02, 04-03, 04-04]
status: complete
commits: 2
plan_head_before: 835953fa36527260ab3bf2f0a1c762cf3333863f
plan_head_after: 28bc7f5200ec71d039fb4e4e749f4b8d9567d87d
actuals:
  tokens: 30000
  tasks: 2
  commits: 2
tech-stack:
  added: []
  patterns:
    - "Transport abstraction so the driver is tested with a scripted transport and an in-process Kestrel fake TV"
    - "Per-TV keyed semaphore serialises pair / power off / power on"
key-files:
  created:
    - src/Cafe.Server/Devices/TvModels.cs
    - src/Cafe.Server/Devices/TvOptions.cs
    - src/Cafe.Server/Devices/ITvDriver.cs
    - src/Cafe.Server/Devices/ITvTransport.cs
    - src/Cafe.Server/Devices/SamsungTvTransport.cs
    - src/Cafe.Server/Devices/WakeOnLan.cs
    - src/Cafe.Server/Devices/SamsungTizenDriver.cs
    - src/Cafe.Server/Devices/TvPairingService.cs
    - src/Cafe.Server/Devices/TvExpiryActionHandler.cs
    - src/Cafe.Server/Devices/TvServiceCollectionExtensions.cs
    - tests/Cafe.Tests/Infrastructure/FakeSamsungTv.cs
    - tests/Cafe.Tests/Infrastructure/FakeTvTransport.cs
    - tests/Cafe.Tests/TvTracerTests.cs
    - tests/Cafe.Tests/TvDriverTests.cs
  modified:
    - src/Cafe.Server/Program.cs
    - tests/Cafe.Tests/Infrastructure/CafeWebApplicationFactory.cs
key-decisions:
  - "No-token marker is the non-blank string '-' because StationService cleans blank strings to null"
  - "Certificate bypass exists only in SamsungTvTransport on the ClientWebSocket (grep-gated)"
  - "GetStateAsync is not serialised per TV so monitor polls never wait behind a 30 s pairing; pair, power off and power on are"
requirements-completed: [TVCT-01, TVCT-02]
---

# Phase 4 Plan 1: Samsung TV driver tracer and hardening Summary

Samsung Tizen TV driver (pair, REST power state, KEY_POWER off, Wake-on-LAN on) over an ITvTransport, proven against an in-process fake TV, with a TvExpiryActionHandler that powers a console's TV off when its session expires.

## Tasks

1. Tracer (b7e0873): full stack plus FakeSamsungTv; pairing stores the token, an expired console session sends exactly one KEY_POWER and the fake ends in standby.
2. Hardening (28bc7f5): PowerOn with 12 magic-packet datagrams (255.255.255.255 and the /24 broadcast, ports 9 and 7, 3 rounds), FakeTvTransport, 49 unit tests for pairing, state parsing, power off rules, concurrency, WoL, handler guards.

Build: 0 warnings. Full suite: 530 passed. No csproj changes, no new packages.

## Deviations from Plan

**1. [Rule 1 - Bug] Fake TV JSON was camel-cased.** WriteAsJsonAsync serialised PowerState as powerState, so the driver read "no PowerState" (On). Fixed by writing literal JSON in FakeSamsungTv (test code only).

**2. [Rule 3 - Blocking] Added TvOptions.KeyFlushMilliseconds (default 300).** The plan's "short moment" before closing the channel needed a configurable value so unit tests can set it to 0. The tracer keeps the default.

**3. GetStateAsync is not behind the per-TV lock.** Plan said every driver call; locking state reads would let a 30 s pairing stall the monitor. Pair, power off and power on are serialised, and the internal re-checks run inside the lock. TVCT-03 is only half delivered here (driver half; the session sink is plan 02), so it is not marked complete.

## Known Stubs

None.

## Human checks pending

- Real Samsung TV (not available): confirm wss://{ip}:8002 with its self-signed certificate accepts the connection, the pairing prompt returns a token, and KEY_POWER powers it off.
- Enable "Power On with Mobile" / network standby on the TV so Wake-on-LAN works; confirm the /24 broadcast assumption matches the cafe subnet.
- Model without PowerState: confirm behaviour on the cafe's actual TV models.

## Threat Flags

None beyond the plan's threat model (certificate callback scoped to one file; token never logged).

## Self-Check: PASSED

All 14 created files exist; commits b7e0873 and 28bc7f5 exist; build and tests green.
