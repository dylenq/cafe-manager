# Roadmap: Cafe Manager

## Overview

Cafe Manager is built as seven vertical phases. It starts with a bootable admin app and station setup, then adds the session and pricing engine (the heart of the core value), then the two enforcement paths (PC agent and Samsung TV control), then customers/packages, POS/receipts, and finally reports and Windows deployment. Each phase ends with something staff can use and verify.

## Phases

- [ ] **Phase 1: Foundation** - Bootable admin app with login, station types, stations and an empty live dashboard
- [ ] **Phase 2: Session Engine & Pricing** - Timed sessions, rate rules, desk alerts, grace period and audit trail (no devices yet)
- [ ] **Phase 3: PC Agent** - Windows lock-overlay agent, warnings, remote commands and a simulator
- [ ] **Phase 4: Samsung TV Control** - Pair Tizen TVs, power off on expiry, wake on session start
- [ ] **Phase 5: Customers & Packages** - Customer lookup, member pricing, prepaid minute packages
- [ ] **Phase 6: POS & Receipts** - Products, orders, recorded payments and thermal receipts
- [ ] **Phase 7: Reports & Deployment** - Revenue reports, Windows service install and agent installer script

## Phase Details

### Phase 1: Foundation

**Goal**: Staff can log in to a running admin app and configure the cafe's station types and stations
**Mode:** mvp
**Depends on**: Nothing (first phase)
**Requirements**: ADMN-01, ADMN-02, ADMN-03, ADMN-04
**Success Criteria** (what must be TRUE):
  1. On first run staff set an admin password, then log in from any browser on the LAN and stay logged in across page refreshes
  2. Staff can create, edit and delete station types, each with a base hourly rate
  3. Staff can create, edit and delete stations with name, type, kind (PC/Console) and agent machine ID or TV IP/MAC
  4. The dashboard shows a grid of all stations with a status badge (idle, in session, paused, expiring, offline)

**Plans**: TBD
- [x] 01-01-PLAN.md
- [x] 01-02-PLAN.md
- [x] 01-03-PLAN.md
- [ ] 01-04-PLAN.md

**UI hint**: yes

### Phase 2: Session Engine & Pricing

**Goal**: Staff can run and bill timed sessions on any station, with accurate pricing, desk alerts and automatic expiry handling
**Mode:** mvp
**Depends on**: Phase 1
**Requirements**: SESS-01, SESS-02, SESS-03, SESS-04, SESS-05, SESS-06, SESS-07, SESS-08, SESS-09, SESS-10, PRIC-01, PRIC-02
**Success Criteria** (what must be TRUE):
  1. Staff can start a prepaid session for a chosen duration or an open-ended session on an idle station, and the dashboard shows a live countdown or elapsed time
  2. Staff can pause, resume, extend, move to another idle station, and stop a session, and see the final charge; paused time is not billed
  3. Staff can define time-of-day/day-of-week rate rules and a session crossing a rule boundary is charged at the correct split rates
  4. The desk UI shows a visible and audible alert as a station enters its warning window or passes expiry, and after the configured grace period the station's expiry action fires automatically
  5. Every session action (start, pause, resume, extend, move, stop, expire) appears in an audit trail

**Plans**: TBD
**UI hint**: yes

### Phase 3: PC Agent

**Goal**: Gaming PCs lock and unlock themselves based on session state and staff can control them from the desk
**Mode:** mvp
**Depends on**: Phase 2
**Requirements**: AGNT-01, AGNT-02, AGNT-03, AGNT-04, AGNT-05, AGNT-06, AGNT-07
**Success Criteria** (what must be TRUE):
  1. A PC running the agent registers by machine ID and shows online/offline on the dashboard
  2. With no active session the PC shows a full-screen lock overlay that Alt+Tab, Win, Alt+F4 and Task Manager cannot escape
  3. Starting a session unlocks the PC; stopping or expiring it (after grace) locks it again, with countdown warnings at 10, 5 and 1 minutes and during grace
  4. Staff can send lock, unlock, message, shutdown and restart to a PC from the dashboard
  5. When the server is unreachable the agent stays locked and reconnects automatically; the simulator can run N fake agents to exercise the whole flow without Windows machines

**Plans**: TBD
**UI hint**: yes

### Phase 4: Samsung TV Control

**Goal**: Console stations stop being usable when paid time runs out and start up when a session begins
**Mode:** mvp
**Depends on**: Phase 2
**Requirements**: TVCT-01, TVCT-02, TVCT-03, TVCT-04
**Success Criteria** (what must be TRUE):
  1. Staff can pair a Samsung Tizen TV from Settings by accepting the prompt on the TV, and the token is stored for that station
  2. When a console session expires and grace ends, the TV powers off automatically
  3. Starting a console session wakes the TV with Wake-on-LAN
  4. The dashboard shows each TV's reachability/power state and staff can power it on or off manually

**Plans**: TBD
**UI hint**: yes

### Phase 5: Customers & Packages

**Goal**: Staff can look up customers, apply member pricing and sell and use prepaid time packages
**Mode:** mvp
**Depends on**: Phase 2
**Requirements**: CUST-01, CUST-02, CUST-03, CUST-04, CUST-05, CUST-06, PRIC-03
**Success Criteria** (what must be TRUE):
  1. Staff can create and edit customers (name, phone, member flag, notes) and find them by searching name or phone
  2. Staff can define time packages (minutes, price, optional station-type limit) and sell one to a customer, adding minutes to their balance and recording the payment
  3. Staff can start a session that draws time from a customer's minute balance
  4. A session linked to a member is charged the member rate and a guest session is charged the guest rate

**Plans**: TBD
**UI hint**: yes

### Phase 6: POS & Receipts

**Goal**: Staff can sell food and drinks, record payments and print receipts
**Mode:** mvp
**Depends on**: Phase 5
**Requirements**: POS-01, POS-02, POS-03, POS-04
**Success Criteria** (what must be TRUE):
  1. Staff can manage products (name, category, price, active) and ring up an order attached to a session, a customer or a walk-in
  2. Staff can record a payment (cash or card, amount) for session charges, orders and packages
  3. Staff can print a receipt on a network ESC/POS thermal printer, falling back to a Windows printer when it is unavailable

**Plans**: TBD
**UI hint**: yes

### Phase 7: Reports & Deployment

**Goal**: The owner can see revenue and the system installs on the cafe's real Windows machines
**Mode:** mvp
**Depends on**: Phase 6
**Requirements**: REPT-01, REPT-02, DEPL-01, DEPL-02
**Success Criteria** (what must be TRUE):
  1. Staff can view revenue by day and by week
  2. Staff can view revenue broken down by station type, by product and by payment method
  3. The server installs and runs as a Windows service on the front-desk PC and the admin UI is reachable from other devices on the LAN
  4. Running the installer script on a gaming PC sets up the logon scheduled task, Task Manager policy and server URL config so the agent starts locked at logon

**Plans**: TBD
**UI hint**: yes

## Progress

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Foundation | 3/4 | In Progress | - |
| 2. Session Engine & Pricing | 0/TBD | Not started | - |
| 3. PC Agent | 0/TBD | Not started | - |
| 4. Samsung TV Control | 0/TBD | Not started | - |
| 5. Customers & Packages | 0/TBD | Not started | - |
| 6. POS & Receipts | 0/TBD | Not started | - |
| 7. Reports & Deployment | 0/TBD | Not started | - |
