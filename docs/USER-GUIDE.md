# Cafe Manager: Staff Guide

This guide is for front-desk staff. It covers a normal day at the desk. Installation and setup are in `INSTALL.md`.

## Logging in

1. Open the Cafe Manager address on the front-desk screen (your owner will have bookmarked it, for example `http://192.168.1.10:5080`).
2. Type the shared password in "Password" and press "Log in".
3. If you type the password wrong several times in a row (5 times within 5 minutes), that device is blocked for 5 minutes. Wait, then try again.

The menu at the top gives you: Dashboard, Stations, Station types, Sessions, Reports, POS, Products, Customers, Packages, Rate rules and Settings. Stations, Station types, Rate rules, Products, Packages and Settings are normally changed by the owner only.

There is no "forgot password" button. If nobody knows the admin password, ask the owner; it cannot be reset from the app.

## The dashboard

The Dashboard shows one card per station, updated live. Each card shows:

- The station name and a status badge: Idle (free), In session, Paused, Expiring (time is nearly up or up), or Offline.
- For a running session: the time left (or time used for an open-ended session), the charge so far, and a "Member" badge if a member customer is attached.
- "Agent offline" on a PC card means the PC's agent is not connected to the server (see Troubleshooting).
- For a console: the TV indicator ("TV On", "TV Standby", "TV Unreachable", "TV Unknown" or "TV not checked") and the buttons "TV On" and "TV Off".
- For a PC: a "PC" menu with Lock, Unlock PC (free), Restart, Shutdown and Send message.

Alerts for stations that are nearly out of time or out of time appear in a strip at the top. Press "Acknowledge" on an alert to tell the team you have seen it; the alert dims but the station stays marked. If the sound is blocked by the browser, press "Enable sound" once.

## Starting a session

On the station's card (the station must be Idle):

- Prepaid time: pick one of the quick buttons (30, 60, 120 or 180 minutes), or type a number in the "min" box under "Start prepaid" and press "Start". The customer pays for the time.
- Open-ended: press "Start open-ended". The clock counts up and the customer pays at the end.
- From a customer's balance: choose the customer under "Customer (optional)" (search by name or phone, or "Quick add"), enter the minutes and press "Start from balance". The minutes are taken from the customer's prepaid time.

If you attach a member customer, the member rate (if the owner set one) applies. For a PC, the PC unlocks within a moment. For a console, the TV is switched on.

## Extending, pausing and moving

All of these are on the station card while a session is running.

1. Extend: press one of the quick buttons (15, 30 or 60 minutes), or type the minutes in the "min" box next to "Extend" and press "Extend" (prepaid sessions). With a customer's balance, use "Extend from balance". You can extend while the session is paused or in the grace period (see below).
2. Pause and resume: press "Pause" to stop the clock (for example the customer steps out), "Resume" to continue. Paused time is not charged.
3. Move: pick another free station under "Move to..." and press "Move". The session continues on the new station. A session paid from a balance for one type of station cannot be moved to a different type.

## Stopping a session

1. Press "Stop" on the station card. A panel shows the final charge, including any food and drinks on the tab.
2. Choose one of:
   - "Stop & settle": stop the session and take payment now (see the next sections).
   - "Stop, pay later": stop the session and leave the amount unpaid on an open tab. It shows under "Open tabs" on the POS page until someone settles it.
   - "Cancel": go back; the session keeps running.
3. If the charge changed while you looked at it (because time went by), the panel tells you. Check the new amount and confirm again.

After the session stops, the PC locks, or the TV is switched off (the owner can disable the TV power-off in Settings).

## Alerts and what happens when time runs out

1. Before the end, the customer sees warning messages on their PC (small toasts in the corner), and the station shows on your alert strip. The warning times (for example 10, 5 and 1 minutes before the end) are set under Settings, "Session expiry".
2. When the time reaches zero, the card shows "TIME UP" and the grace period starts. The PC toast turns red and says "Time is up - save your work". The grace length is also set under Settings (default 2 minutes).
3. If you extend the session during grace, it carries on. Otherwise, when grace ends, the session ends: the PC locks, or the TV is powered off.
4. If the server or network drops mid-session, the PC keeps counting down locally and locks itself at zero.

An open-ended session never runs out by itself. It only ends when you stop it.

## Customers and packages

Customers page:

- Find someone: type a name or phone number in "Search" and press "Search".
- New customer: "Add customer". Fill in Name, Phone, Notes, and tick "Member" for members. "Edit" changes details. Tick "Show inactive" to see deactivated customers.
- Click a customer to open their page. It shows their Balance (minutes, per station type or any type), Package purchases, Ledger history, Receipts and Recent sessions.

Selling a package (prepaid time bundle, for example "10 hours"):

1. Open the customer's page.
2. Under "Sell package" choose the Package and press "Sell package".
3. The payment window opens. Take payment (see "Tabs, settling up and receipts"). The minutes are added to the customer's balance when the sale is paid.

Balance minutes are held in buckets: some are only for one type of station (for example PC only), others are for "Any type". A session started from balance uses minutes that match the station.

"Manual adjustment" (customer page): choose the Bucket, enter Minutes (+ to add, - to remove) and a required Note, then press "Adjust balance". Use it to fix mistakes. The note is kept in the ledger history.

## Selling food and drinks (POS)

The POS page lists products by category (buttons along the top, "All" shows everything).

1. Tap a product to add it to the Cart. Use + and - to change the quantity, "Remove" to take a line out, and "Clear" to empty the cart. Add an optional "Note (optional)".
2. Decide where the order goes:
   - Walk-in: choose "Walk-in" and press "Charge now". The payment window opens.
   - Onto a running session's tab: choose the "Active session" (the customer is filled in for you) and press "Add to tab". Nothing is paid yet; the items are added to what the customer owes when the session is settled.
3. If a product is missing, ask the owner to add it on the Products page.

## Tabs, settling up and receipts

The "Open tabs" panel on the POS page lists everything owed that has not been paid, for example a session you stopped with "Stop, pay later" or food added to a session. A row marked "Session still running" is still counting.

To take payment ("Settle", or "Stop & settle" on a station card):

1. The payment window shows "Total due". Choose the Method (Cash or Card) and the amount.
2. Cash: press "Exact cash" if the customer pays the exact amount, or type what they hand over. The window shows the change.
3. Card: type the card amount, or press "Rest by card" to put the remainder on the card.
4. To split a payment (part cash, part card) press "Add payment" and add another line.
5. Press "Record payment". If something is still owed afterwards the window says "Remaining outstanding" and the rest stays on the tab. If nothing is due ("Nothing to pay"), record the free sale.
6. The window then shows the receipt number and any "Change due". Press "Print receipt" to print it (in browser mode a print window opens for you to confirm), then "Done". Past receipts are in "Recent receipts" on the POS page (button "Reprint"), and on the customer's page under Receipts.

Voiding: in "Open tabs", press "Void" on an unpaid order and then "Yes" to confirm ("No" to keep it). Only unpaid orders can be voided.

## Reports

Open Reports from the menu.

1. Choose a "Range": Today, Yesterday, This week, Last 7 days, This month or Last month, then press "Apply". Weeks start on Monday.
2. The Summary tiles show: Total collected, Cash, Card, Sessions, Session hours and Average session length, and "Charged (unpaid)".
3. Below are tables: Revenue by day, Revenue by week, By station type, By category, By product and By payment method. Each has an "Export CSV" link that downloads that table for a spreadsheet.

How the numbers work:

- Reports count money on the day it was collected (a "cash basis"). A payment taken at 23:30 counts on that calendar day in the cafe's time zone, even for a session that began earlier.
- "Charged (unpaid)" is money owed but not yet collected, for example a session stopped with "Stop, pay later". When it is paid, it moves into the collected numbers on the day of payment.
- If one order is only partly paid, the money collected is split between its items in proportion to their prices. This affects the By product, By category and By station type tables, and the totals always add up to the money actually collected.

## Troubleshooting

### A PC shows offline

The card shows "Agent offline" and the PC menu is disabled.

1. Check the PC is switched on and the gaming user is logged in. The agent only runs while that user is logged in.
2. Check the PC's network cable or Wi-Fi.
3. Check the server PC is on. If the dashboard itself shows "Live data is temporarily out of date", the problem is the connection to the server; refresh the page.
4. If it still shows offline, restart the PC. The agent starts again at logon.
5. If it stays offline, tell the owner. Common causes are a wrong server address, a changed shared key, or a station whose "Agent machine ID" does not match the PC (see `INSTALL.md`).

While the agent is offline, a locked PC stays locked. If the server is unreachable and there is no session, the PC locks.

### A TV does not respond

1. Look at the TV indicator on the console's card. "TV Unreachable" means the server cannot find the TV on the network.
2. Check the TV has power and is on the cafe network. Some TVs only answer when on or in network standby; one that is fully off may show "Unreachable" until switched on by hand.
3. Press "TV On" or "TV Off" again. Switching off a TV during a running session asks you to press "Confirm".
4. If the TV was reset, or it asks for permission, the owner needs to press "Re-pair TV" on the station and accept the request on the TV screen. If the TV got a new IP address, the owner updates "TV IP address" on the Stations page.

### Emergency unlock

If a PC is locked and the server cannot be reached (for example the server PC is off), staff can unlock it by hand.

1. On the locked PC press Ctrl+Alt+Shift+F12. A code prompt appears.
2. Type the emergency code (set when the agent was installed; ask the owner).
3. After 3 wrong codes the prompt locks for 30 seconds, and each further round of wrong codes locks it for twice as long (up to 15 minutes). Wait and try again carefully.
4. A correct code unlocks the PC until the next message from the server. If no code was configured, the prompt says unlock is not configured.

Emergency unlock is for emergencies only. For a normal unlock while the server is working, use the PC menu on the dashboard: "Unlock PC (free)".

To lock or restart a PC from the desk, use the "PC" menu: Lock, Restart, Shutdown (these ask you to confirm), and "Send message" to show a text message on that PC. The Sessions page, tab "Agent commands", lists every command that was sent.
