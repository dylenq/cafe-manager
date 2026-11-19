# Cafe Manager

## What This Is

Cafe Manager is local management software for a small gaming cafe (under 15 stations): Windows gaming PCs plus consoles connected to Samsung Tizen smart TVs. Front-desk staff use one browser-based admin app to start and bill timed sessions, lock and unlock PCs, power off console TVs when time runs out, manage members and prepaid time, sell food and drinks, print receipts and see revenue. It runs entirely on a server in the cafe, so it keeps working when the internet drops.

## Core Value

Staff can start a timed session on any station, and that station reliably stops being usable when the paid time runs out: the PC locks or the TV powers off.

## Business Context

- **Customer**: The cafe owner and front-desk staff (single shared login); walk-in guests and members are the cafe's customers.
- **Revenue model**: Hourly station time (per station type, with time-of-day and member pricing), prepaid time packages, food and drinks.
- **Success metric**: Every minute played is billed; no station keeps running past paid time without staff knowing.

## Requirements

### Validated

(None yet — ship to validate)

### Active

- [ ] Station management: fewer than 15 stations, each with a type (PC, PS5, VIP PC...) that has its own hourly rate
- [ ] Staff start sessions from the desk: prepaid (fixed time or drawn from a package) or open-ended (billed when the customer leaves)
- [ ] Staff can pause/resume, extend and move a session to another station
- [ ] Pricing: rate per station type, time-of-day/happy-hour rules, member vs guest rates
- [ ] Prepaid time packages stored as minutes on the customer's account
- [ ] End of session: on-screen warnings on PCs at 10, 5 and 1 minutes, desk alert, grace period, then auto-lock/power-off
- [ ] PC agent: full-screen lock overlay; admin can lock, unlock, message, shut down, restart
- [ ] Console stations: Samsung Tizen TV powered off over its local WebSocket API on expiry, woken with Wake-on-LAN when a session starts
- [ ] Customers: staff look them up by name or phone; member flag
- [ ] Food/drink POS: products, orders on a session/customer/walk-in
- [ ] Payments recorded (cash or card-terminal method plus amount), with no payment-provider integration
- [ ] Thermal receipt printing (ESC/POS)
- [ ] Revenue reports by day/week, station type and product
- [ ] Single shared admin login
- [ ] Server runs as a Windows service on the front-desk PC; admin UI is used from a browser on the LAN

### Out of Scope

- Integrated card payments / online top-ups — the cafe uses a separate card terminal; record only
- Customer self-login at PCs — staff start every session from the desk
- Multiple staff roles/permissions — a single shared login was chosen
- Inventory/stock tracking and shift cash-up — not needed for v1
- Multi-location / cloud sync — single cafe, local only
- Hardened kiosk shell replacement — a full-screen overlay plus key blocking is enough for v1
- On-screen warnings on console TVs — Samsung Tizen has no reliable messaging API; console warnings go to desk alerts instead
- Smart-plug control — the TVs themselves are controlled instead

## Context

- Greenfield project in an empty folder. Development happens on macOS; the server and PCs in production are Windows.
- Samsung Tizen remote API: `wss://{ip}:8002/api/v2/channels/samsung.remote.control`. The first connection needs approval on the TV and returns a token to store. `http://{ip}:8001/api/v2/` reports device info and power state. Power-on needs WoL with "Power On with Mobile"/network standby enabled.
- Customers can turn a TV back on with its remote, so remotes should stay behind the desk; desk alerts flag stations still running after expiry.
- The PC agent is a WPF app launched at logon by Task Scheduler, with a low-level keyboard hook and a Task Manager policy lock while locked. It defaults to locked when the server is unreachable.
- An agent simulator (console app) lets the PC flow be tested on macOS without Windows machines.

## Constraints

- **Tech stack**: C# / .NET 10 — the user's choice, and a strong fit for the Windows agent and the Windows-service host.
- **Server**: ASP.NET Core + Blazor (interactive server), EF Core + SQLite, SignalR — local, single machine, no external DB to run.
- **Agent platform**: Windows only (WPF); it can't be run on the macOS dev machine, so the simulator covers the PC flow during development.
- **Network**: Local LAN only; it must keep working without internet.
- **Scale**: Fewer than 15 stations, one location; no need to scale beyond that.

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| .NET 10 LTS for server and agent, published self-contained | User's preference; LTS to Nov 2028; self-contained means no runtime install on cafe PCs | — Pending |
| Blazor web admin UI served by the local server | Usable from desk PC, tablet or phone on cafe Wi-Fi | — Pending |
| SQLite database | Zero-admin, single local server, small scale | — Pending |
| SignalR for server ↔ PC agent | Real-time push, auto-reconnect, first-class .NET client | — Pending |
| Full-screen overlay lock (not shell replacement) | Good enough for most cafes, much less work | — Pending |
| Samsung TV power-off + WoL for consoles | TVs are already Samsung smart TVs; no extra hardware | — Pending |
| Staff start all sessions from desk | User's choice; simpler agent, no customer auth at PCs | — Pending |
| Record-only payments | Cafe uses separate card terminal | — Pending |
| Single shared admin login | User's choice; small team | — Pending |

## Evolution

This document evolves at phase transitions and milestone boundaries.

**After each phase transition** (via `/gsd-transition`):
1. Requirements invalidated? → Move to Out of Scope with reason
2. Requirements validated? → Move to Validated with phase reference
3. New requirements emerged? → Add to Active
4. Decisions to log? → Add to Key Decisions
5. "What This Is" still accurate? → Update if drifted

**After each milestone** (via `/gsd:complete-milestone`):
1. Full review of all sections
2. Core Value check — still the right priority?
3. Audit Out of Scope — reasons still valid?
4. Update Context with current state

---
*Last updated: 2026-11-19 after initialization*
