# Requirements: Cafe Manager

**Defined:** 2026-11-19
**Core Value:** Staff can start a timed session on any station, and that station reliably stops being usable when the paid time runs out: the PC locks or the TV powers off.

## v1 Requirements

### Foundation & Admin

- [x] **ADMN-01**: Staff set the admin password on first run and log in with it from any browser on the LAN; the login persists across refreshes
- [x] **ADMN-02**: Staff can create, edit and delete station types with a base hourly rate
- [x] **ADMN-03**: Staff can create, edit and delete stations (name, type, kind PC/Console, agent machine ID or TV IP/MAC)
- [ ] **ADMN-04**: Staff see a dashboard grid of all stations with live status (idle, in session, paused, expiring, offline)

### Sessions

- [ ] **SESS-01**: Staff can start a prepaid session on an idle station for a chosen duration
- [ ] **SESS-02**: Staff can start an open-ended session that runs until stopped and is priced at the end
- [ ] **SESS-03**: Staff can pause and resume a session, and paused time is neither counted nor billed
- [ ] **SESS-04**: Staff can extend a running session by a chosen amount of time
- [ ] **SESS-05**: Staff can move a session to another idle station, keeping its remaining time and charges
- [ ] **SESS-06**: Staff can stop a session and see the final charge
- [ ] **SESS-07**: The dashboard shows a live countdown (or elapsed time for open-ended sessions) per station
- [ ] **SESS-08**: The desk UI shows a visible and audible alert when a station is within the warning window or past expiry
- [ ] **SESS-09**: After expiry a configurable grace period runs, then the station's expiry action fires automatically
- [ ] **SESS-10**: Every session action (start, pause, resume, extend, move, stop, expire) is recorded in an audit trail

### Pricing

- [x] **PRIC-01**: Session cost uses the station type's hourly rate
- [ ] **PRIC-02**: Staff can define time-of-day/day-of-week rate rules (e.g. happy hour), and costs split correctly across rule boundaries
- [ ] **PRIC-03**: Members get member pricing (separate rate or discount) and guests pay the guest rate

### PC Agent

- [ ] **AGNT-01**: An agent on each gaming PC connects to the server, registers by machine ID and shows online/offline on the dashboard
- [ ] **AGNT-02**: When no session is active the PC shows a full-screen lock overlay that Alt+Tab, Win, Alt+F4 and Task Manager can't escape
- [ ] **AGNT-03**: Starting a session unlocks the PC; stopping or expiring it locks the PC again
- [ ] **AGNT-04**: The PC shows countdown warnings at 10, 5 and 1 minutes remaining and during the grace period
- [ ] **AGNT-05**: Staff can send lock, unlock, message, shutdown and restart commands to a PC from the dashboard
- [ ] **AGNT-06**: The agent stays locked when it can't reach the server, and reconnects automatically
- [ ] **AGNT-07**: A simulator can run N fake agents so the PC flow can be tested without Windows machines

### Console TV Control

- [ ] **TVCT-01**: Staff can pair a Samsung Tizen TV from Settings (accept on the TV), and the token is stored for that station
- [ ] **TVCT-02**: When a console session expires (after grace), the TV powers off automatically
- [ ] **TVCT-03**: Starting a console session wakes the TV with Wake-on-LAN
- [ ] **TVCT-04**: The dashboard shows TV reachability/power state and lets staff power it on or off manually

### Customers & Packages

- [ ] **CUST-01**: Staff can create and edit customers (name, phone, member flag, notes)
- [ ] **CUST-02**: Staff can search customers by name or phone
- [ ] **CUST-03**: Staff can define time packages (minutes, price, optionally limited to a station type)
- [ ] **CUST-04**: Staff can sell a package to a customer, which adds minutes to their balance and records the payment
- [ ] **CUST-05**: Staff can start a session that draws time from a customer's minute balance
- [ ] **CUST-06**: Sessions can be linked to a customer so member pricing applies

### POS & Payments

- [ ] **POS-01**: Staff can manage products (name, category, price, active)
- [ ] **POS-02**: Staff can ring up an order and attach it to a session, a customer or a walk-in
- [ ] **POS-03**: Staff can record a payment (cash or card, amount) for session charges, orders and packages
- [ ] **POS-04**: Staff can print a receipt on a network ESC/POS thermal printer, with a fallback to a Windows printer

### Reports & Deployment

- [ ] **REPT-01**: Staff can view revenue by day and by week
- [ ] **REPT-02**: Staff can view revenue broken down by station type, by product and by payment method
- [ ] **DEPL-01**: The server installs and runs as a Windows service on the front-desk PC and is reachable on the LAN
- [ ] **DEPL-02**: An installer script sets up the agent on a gaming PC (logon scheduled task, Task Manager policy, server URL config)

## v2 Requirements

### Operations

- **OPS-01**: Inventory/stock counts with low-stock warnings
- **OPS-02**: Shift open/close with cash drawer totals
- **OPS-03**: Multiple staff accounts with roles

### Customer Experience

- **CX-01**: Customer self-login at PCs drawing from their own balance
- **CX-02**: Reservations/booking stations ahead of time

## Out of Scope

| Feature | Reason |
|---------|--------|
| Integrated card payments / online top-ups | Cafe uses a separate card terminal; record-only |
| Multi-location / cloud sync | Single cafe, local server only |
| Hardened kiosk shell replacement | Overlay plus key blocking is enough for v1 |
| On-screen warnings on console TVs | Samsung Tizen lacks a reliable messaging API; desk alerts instead |
| Smart-plug control | TVs are controlled directly |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| ADMN-01 | Phase 1 | Complete |
| ADMN-02 | Phase 1 | Complete |
| ADMN-03 | Phase 1 | Complete |
| ADMN-04 | Phase 1 | Pending |
| SESS-01 | Phase 2 | Pending |
| SESS-02 | Phase 2 | Pending |
| SESS-03 | Phase 2 | Pending |
| SESS-04 | Phase 2 | Pending |
| SESS-05 | Phase 2 | Pending |
| SESS-06 | Phase 2 | Pending |
| SESS-07 | Phase 2 | Pending |
| SESS-08 | Phase 2 | Pending |
| SESS-09 | Phase 2 | Pending |
| SESS-10 | Phase 2 | Pending |
| PRIC-01 | Phase 2 | Complete |
| PRIC-02 | Phase 2 | Pending |
| AGNT-01 | Phase 3 | Pending |
| AGNT-02 | Phase 3 | Pending |
| AGNT-03 | Phase 3 | Pending |
| AGNT-04 | Phase 3 | Pending |
| AGNT-05 | Phase 3 | Pending |
| AGNT-06 | Phase 3 | Pending |
| AGNT-07 | Phase 3 | Pending |
| TVCT-01 | Phase 4 | Pending |
| TVCT-02 | Phase 4 | Pending |
| TVCT-03 | Phase 4 | Pending |
| TVCT-04 | Phase 4 | Pending |
| CUST-01 | Phase 5 | Pending |
| CUST-02 | Phase 5 | Pending |
| CUST-03 | Phase 5 | Pending |
| CUST-04 | Phase 5 | Pending |
| CUST-05 | Phase 5 | Pending |
| CUST-06 | Phase 5 | Pending |
| PRIC-03 | Phase 5 | Pending |
| POS-01 | Phase 6 | Pending |
| POS-02 | Phase 6 | Pending |
| POS-03 | Phase 6 | Pending |
| POS-04 | Phase 6 | Pending |
| REPT-01 | Phase 7 | Pending |
| REPT-02 | Phase 7 | Pending |
| DEPL-01 | Phase 7 | Pending |
| DEPL-02 | Phase 7 | Pending |

**Coverage:**
- v1 requirements: 42 total
- Mapped to phases: 42
- Unmapped: 0

---
*Requirements defined: 2026-11-19*
*Last updated: 2026-11-19 after roadmap creation*
