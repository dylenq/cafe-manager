# Client videos: narration scripts

Narration used in the two client videos in `dist/video/`. Voice: Kokoro 82M (`af_heart`), generated locally with [audio.cpp](https://github.com/0xShug0/audio.cpp). To re-voice with a human narrator, read each line at the timestamp shown; the matching `.srt` caption files sit next to the videos.

## Guide video (cafe-manager-guide.mp4)

Length: 8:25.

- `0:00` Welcome to Cafe Manager. This video walks you through everything your front desk needs: starting and billing timed sessions, locking PCs and switching off console TVs when time runs out, members and prepaid time, food and drinks, receipts and reports.
- `0:16` Everything runs on one computer inside the cafe, so it keeps working even when the internet is down.

### First, logging in

- `0:25` Open the Cafe Manager address on the front desk computer. Your owner will usually bookmark it for you.
- `0:31` Type the shared staff password and press Log in. After five wrong tries in five minutes, that device is blocked for five minutes, so take your time.

### The dashboard

- `0:41` The dashboard is your home screen. Every station in the cafe has its own card, and the cards update live, so every screen at the desk always shows the same thing.
- `0:51` Along the top, the tiles count free stations, stations in use, those that need attention, and those offline. Tap a tile to filter the dashboard, and tap All stations to see everything again.
- `1:03` The badge in the corner shows the status: Idle means free, In session, Paused, Expiring when time is nearly up, or Offline.
- `1:11` Console stations also show the TV status, with buttons to switch the TV on or off. There is no real TV connected in this demo, so they show Unreachable.
- `1:22` In the bottom corner of this video you can see the screen of a gaming PC. Right now it is locked, waiting for the front desk.
- `1:29` One more thing: press Enable sound once at the start of your shift, so you hear the alarm when a station runs out of time.

### Starting a session

- `1:38` A customer wants one hour on PC 3. On the card, press one of the quick buttons: thirty, sixty, a hundred and twenty, or a hundred and eighty minutes.
- `1:48` The PC unlocks within a second, and the countdown starts on the card. The charge is shown and updates as you go.
- `1:55` For any other length, type the minutes in the box and press Start.

### Extending and pausing

- `2:00` They want more time? Press plus fifteen, plus thirty or plus sixty, or type your own number and press Extend. The new time goes straight to the PC.
- `2:09` If a customer steps out, press Pause. The clock stops, the PC locks, and paused time is not charged.
- `2:15` Press Resume when they are back.

### Members and open-ended sessions

- `2:20` To attach a customer, search by name or phone number in the customer box, and pick them from the list.
- `2:26` Alex is a member, so the member rate is applied automatically.
- `2:30` Press Start open ended when the customer will pay at the end. The clock counts up instead of down, and it never runs out on its own.
- `2:38` Customers can also buy prepaid hours. Jordan has five hours on his balance.
- `2:43` Pick Jordan, check the minutes, and press Start from balance. The time is taken from the balance instead of being paid now.

### Moving a session

- `2:51` A PC has a problem, or friends want to sit together? Open Move to another station, choose a free station, and press Move. The session carries on at the new station with the same time and charge.

### Controlling a PC remotely

- `3:07` Every PC card has a menu with three dots. From here you can lock or unlock the PC, restart it, shut it down, or send a message.
- `3:15` Let's tell PC 3 that their noodles are ready.
- `3:22` The message pops up in the corner of the customer's screen, without interrupting their game.

### When time runs out

- `3:29` Now the most important part: what happens when paid time runs out. Let's start a very short one minute session on PC 1, so we can watch it.
- `3:38` Before the end, the customer sees a countdown in the corner of their screen. Warnings are shown at ten, five and one minutes before the end. At the desk, the station appears in the alert strip at the top.
- `3:51` Press Acknowledge to tell the rest of the team you have seen it.
- `3:59` At zero, the card shows Time up and a short grace period begins. The customer's countdown turns red, telling them to save their work. If they want more time, just press Extend now and nothing is interrupted.
- `4:18` When the grace period ends, the PC locks itself. For a console, the TV is switched off instead.
- `4:24` And this also works if the network goes down: the PC keeps counting on its own and still locks at zero. The amount owed is waiting on the POS page as an open tab.

### Stopping and taking payment

- `4:36` PC 3 is done. Press Stop. The panel shows the final charge, including any food and drinks added to their tab.
- `4:44` Press Stop and settle to take payment now.
- `4:46` You can split a payment. Here the customer pays five dollars in cash, so choose Cash, enter the amount, and press Add payment.
- `4:54` Then press Rest by card for the remainder, and Record payment.
- `4:59` The payment is saved and a receipt number is created. You can print it, or press Done. The PC locks straight away.
- `5:06` If someone will pay later, press Stop, then Stop, pay later. The amount stays on an open tab until it is settled.

### Food and drinks

- `5:17` Open the POS page to sell food and drinks. Tap a product to add it to the cart, and use plus and minus to change the quantity.
- `5:25` For a walk-in customer, press Charge now. When paying cash, type what they hand you, and Cafe Manager shows the change.
- `5:35` To put items on someone's session instead, choose Active session, pick the station, and press Add to tab. It will be added to their bill when the session stops.
- `5:45` Open tabs are listed on the right. Press Settle to take payment for any of them, like the session that expired earlier.

### Customers and packages

- `5:56` On the Customers page you can search, add and edit customers. Tick Member when you add a member.
- `6:02` Open a customer to see their balance, purchases, receipts and recent sessions.
- `6:07` To sell a package of hours, choose the package and press Sell package. Take the payment, and the minutes are added to their balance as soon as it is paid.

### Sessions and reports

- `6:19` The Sessions page lists every session, with its start and end, why it ended, and what it cost. Press Events to see exactly what happened during a session.
- `6:31` Reports show the money collected for any period: today, this week, this month, or a custom range, split into cash and card.
- `6:38` Scroll down for revenue by day and week, by station type, by product and category, and by payment method. Every table can be exported to a spreadsheet with Export CSV.

### Setup, for the owner

- `6:51` The pages under Setup are normally used by the owner only.
- `6:57` Station types set the hourly price, with an optional cheaper member price.
- `7:03` Stations is where each PC and console is added. A PC is linked to its agent by its machine ID. A console needs the TV's network address, and is paired once with the Pair TV button.
- `7:18` Products are the food and drinks sold on the POS page, grouped by category.
- `7:25` Packages are prepaid bundles of hours, for one station type or for any station.
- `7:32` Rate rules change the price at certain times, for example a weekend evening price, or a cheaper rate in the morning.
- `7:41` Settings holds the grace period and warning times, the key used to connect gaming PCs, the TV power-off option, the receipt printer, and the nightly backups.

### If something goes wrong

- `7:54` If a PC card says Agent offline, check that the PC is switched on and connected to the network. It will reconnect by itself. If a TV shows Unreachable, check it is plugged in and on the cafe network.
- `8:08` And if a PC is ever stuck locked with the server down, staff can use the emergency unlock code on the lock screen. Ask your owner for the code. The full staff guide is included with the software.
- `8:20` That's it. You are ready to run the front desk with Cafe Manager.

## Promo video (cafe-manager-promo.mp4)

Length: 1:13.

- `0:00` Cafe Manager. The front desk for your gaming cafe.
- `0:04` Every PC and every console, live on one screen.
- `0:07` Start a session with one tap, and the PC unlocks instantly.
- `0:11` Members, prepaid hours and open-ended play are priced automatically.
- `0:20` Snacks and drinks go straight onto the customer's bill.
- `0:28` And when time runs out?
- `0:36` The customer is warned, gets a short grace period, and then the PC locks itself. Consoles switch off their TV.
- `0:48` Stop, settle in cash or card, and print a receipt.
- `0:58` See your revenue by day, by product and by payment method.
- `1:02` It runs on your own server in the cafe, so it keeps working even when the internet is down.
- `1:07` Cafe Manager. Simple, reliable, and built for your cafe.
