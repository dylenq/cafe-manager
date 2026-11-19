---
status: testing
phase: 01-foundation
source: [01-VERIFICATION.md]
started: 2026-11-20T00:00:00Z
updated: 2026-11-20T00:00:00Z
---

## Current Test

number: 1
name: LAN access from a phone/tablet with persistent login
expected: |
  http://<server-ip>:5080 is reachable from another device on the cafe LAN; after logging in,
  the session survives a refresh and a browser close/reopen (30-day sliding cookie).
awaiting: user response

## Tests

### 1. LAN access from a phone/tablet with persistent login
expected: Reachable over LAN at http://<server-ip>:5080; login persists across refresh and browser restart
result: [pending]
note: Needs a real second device. Localhost login and the persistent cookie were confirmed in the in-app browser on 2026-11-20.

### 2. Phone-width layout
expected: One column below 576px; nav collapses; no horizontal scroll
result: pass
note: Checked by Claude in the in-app browser at 375x812 on 2026-11-20. Cards stacked in one column, nav collapsed to a toggle, scrollWidth equal to the viewport width.

### 3. Dashboard live refresh
expected: An edit in another tab appears on / within about 5s without reload
result: pass
note: Checked by Claude on 2026-11-20. A rename in a second tab showed up on the dashboard tab within 7s with no reload.

### 4. Kind toggle on /stations
expected: PC shows Agent machine ID; Console shows TV IP/MAC fields
result: pass
note: Checked by Claude on 2026-11-20. Switching Kind to Console swapped the Agent machine ID field for the TV IP and MAC fields. A Console station with a TV IP saved and shows Offline on the dashboard.

### 5. Five status badges visually distinct
expected: Idle, In session, Paused, Expiring, Offline are visibly distinct
result: [pending]
note: Idle (grey) and Offline (dark) were seen and are distinct. In session, Paused and Expiring can't be produced until Phase 2 adds sessions, so check them then.

## Summary

total: 5
passed: 3
issues: 0
pending: 2
skipped: 0
blocked: 0

## Gaps
