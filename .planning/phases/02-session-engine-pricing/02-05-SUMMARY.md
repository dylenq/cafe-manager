---
phase: 02-session-engine-pricing
plan: 05
subsystem: desk-ui
tags: [blazor, web-audio, alerts, razor-css-isolation]
requires:
  - phase: 02-session-engine-pricing
    provides: SessionService, GetStopQuoteAsync, SessionClock, ExpiryEvaluator, SessionNotifier (plans 01-04)
provides:
  - Full StationCard controls (start prepaid/open-ended, pause/resume, extend, move, stop with final-charge confirmation)
  - TIME UP and grace countdown rendering with pulsing styles (reduced-motion aware)
  - AlertTracker (beep once per level change, acknowledge), SessionAlerts (AlertLevel)
  - AlertStrip, SoundToggle, wwwroot/js/alerts.js (window.cafeAlerts)
  - Cross-plan end-to-end tests for the phase success criteria
affects: [03, 04]
actuals:
  tokens: 40000
  tasks: 3
  commits: 3
commits: 3
plan_head_before: 81e6bcb
plan_head_after: fb73102d568abdf297e5743e837cb904ec247fbe
tech-stack:
  added: []
  patterns:
    - Pure AlertTracker/SessionAlerts keep alert logic out of Razor and JS so it is unit-tested
    - Home evaluates alerts on every 1-second tick and every data refresh; beeps only when RendererInfo.IsInteractive
    - All JS interop wrapped in try/catch; localStorage access inside try with in-memory fallback
key-files:
  created:
    - src/Cafe.Server/Dashboard/{AlertTracker,SessionAlerts}.cs
    - src/Cafe.Server/Components/Shared/{AlertStrip,SoundToggle}.razor
    - src/Cafe.Server/Components/Shared/StationCard.razor.css
    - src/Cafe.Server/wwwroot/js/alerts.js
    - tests/Cafe.Tests/{AlertTrackerTests,DashboardSessionCardsTests}.cs
  modified:
    - src/Cafe.Server/Components/Shared/{StationCard,StatusBadge}.razor
    - src/Cafe.Server/Components/Pages/Home.razor
    - src/Cafe.Server/Components/Layout/MainLayout.razor
    - src/Cafe.Server/Components/App.razor
    - src/Cafe.Server/Dashboard/StationCardModel.cs
    - src/Cafe.Server/wwwroot/app.css
    - tests/Cafe.Tests/SessionTracerTests.cs
key-decisions:
  - "AlertStrip takes a Details dictionary (remaining/grace text) computed in Home so AlertInput stays (StationId, Name, Level)."
  - "Acknowledged state is passed to cards as a parameter: acknowledged cards stay flagged but stop pulsing."
  - "Prerender/first load updates the tracker without sound; after interactivity a station already alerting counts as new once."
status: complete
---

# Phase 2 Plan 05: Desk UI and Alerts Summary

Dashboard cards now drive the whole session lifecycle with a final-charge stop confirmation, show TIME UP with a grace countdown, and alert the desk visibly (pulsing card, badge, alert strip with Acknowledge) and audibly (Web Audio beep, mute toggle, unlock button, no audio files or CDN).

## What was built

- **StationCard**: idle/offline-without-session cards show Start prepaid (30/60/120/180 plus custom 1-1440) and Start open-ended; session cards show mode, countdown or elapsed, charge (est. for open-ended), badge, Pause/Resume, Extend (+15/+30/+60/custom, hidden for open-ended), Move (idle enabled stations, disabled in Grace) and Stop. Stop first calls GetStopQuoteAsync and shows "Final charge: X" with Confirm/Cancel; on confirm Home shows a dismissible notice "{station} stopped. Final charge: X". Buttons disable while a call is in flight; exceptions are logged and shown as "Something went wrong. Try again."
- **SessionAlerts / AlertTracker**: pure helpers; Warning inside the warning window, TimeUp in Grace or past expiry, Paused and open-ended never alert.
- **alerts.js**: cafeAlerts.unlock/isUnlocked/isMuted/setMuted/beep; 880 Hz single beep for warning, three 1200 Hz beeps for time up; localStorage key cafe.alerts.muted inside try/catch with memory fallback.
- **SoundToggle** in the layout top row; **AlertStrip** with role=alert/aria-live polite and Acknowledge buttons.
- **Tests**: AlertTrackerTests (7), DashboardSessionCardsTests (9 including two end-to-end scenarios: prepaid warning -> TIME UP -> grace elapsed with handler called once and ordered audit events; open-ended pause/resume/move/stop with charge 2500.00 and full audit trail). Full suite: 300 passing.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Existing tracer test asserted the removed plan-01 button**
- **Found during:** Task 2 full-suite run
- **Issue:** SessionTracerTests expected "Start 60 min" and located cards by `>name<`, which now also matches Move options and the alert strip.
- **Fix:** Assert "Start open-ended" and locate the card via `>name</h2>`.
- **Files modified:** tests/Cafe.Tests/SessionTracerTests.cs
- **Commit:** 2ab4f89

No other deviations.

## Human checks pending

Audio and animation cannot be verified automatically. With `dotnet run --project src/Cafe.Server`: log in, set warning minutes to 1 in /settings (short grace), start a 1-minute prepaid session, click "Enable sound", and confirm: card border and badge pulse and the alert strip lists the station when the warning window starts; one short beep; Acknowledge silences repeats; the mute toggle stops sound and survives refresh; at expiry a triple beep plays with TIME UP and a grace countdown.

## Known Stubs

None.

## Threat Flags

None. New surface is client-side only (alerts.js, no network, no external URLs); all text goes through Razor encoding (no MarkupString).

## Self-Check: PASSED

Files exist, commits a584e23, 2ab4f89, fb73102 present, `dotnet build` and `dotnet test` (300 passing) clean, `node --check alerts.js` exits 0.
