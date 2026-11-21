CAFE MANAGER - FRONT DESK PC
============================

This package installs Cafe Manager on the front-desk computer. That computer
becomes the "server": it runs the program that times sessions, locks the gaming
PCs and turns the TVs off. Staff use it from a web browser.

Keep this PC switched on whenever the cafe is open.


BEFORE YOU START
----------------
- Use a Windows 10 or 11 PC that is connected to the cafe network (same router
  as the gaming PCs and TVs). No internet is needed to run the cafe.
- Sign in to Windows with an account that has administrator rights.
- Recommended: in your router settings, give this PC a fixed (reserved) IP
  address, so the gaming PCs can always find it.


INSTALL (about 2 minutes)
-------------------------
1. Copy this zip to the front-desk PC.
2. Right-click the zip > Properties > tick "Unblock" (if you see it) > OK.
3. Right-click the zip > Extract All... > Extract.
4. Open the extracted folder and double-click
       Install Cafe Manager Server
5. Windows asks "Do you want to allow this app to make changes?" - click Yes.
   If a blue "Windows protected your PC" box appears, click "More info",
   then "Run anyway".
6. Wait until the window says "Done. The server is running."
   Write down the address it shows, for example  http://192.168.1.10:5080
   You will need it for the gaming PCs and for other devices.
7. A browser opens on this PC. Choose the admin password (at least 8
   characters). Keep it safe - it cannot be reset.

The program now starts by itself every time this PC starts. You do not need
to open anything after a restart.


FIRST SETUP IN THE APP
----------------------
Log in, then in this order:
1. Station Types - add your types and hourly prices (for example "PC" and
   "PS5"), with an optional member price.
2. Stations - add every PC and console. For a console, enter the TV's IP
   address and MAC address.
3. Settings > PC agents - this shows the "Shared key". You need it once for each
   gaming PC (see README-GamingPC.txt in the gaming PC package).
4. Settings > Receipts - your cafe name, address and printer.
5. Products, Packages and Rate Rules (happy hour) - when you want them.

Samsung TVs: on each TV turn on Settings > General > Network > Expert Settings
> "Power On with Mobile" and "IP Remote". Then in the app go to Settings >
Samsung TVs, press "Pair TV" for that console and accept the request on the
TV screen.


USING IT EVERY DAY
------------------
- On this PC open  http://localhost:5080
- On a tablet, phone or other PC in the cafe, open the address from step 6.
- The full staff guide is in docs\USER-GUIDE.md.


BACKUPS
-------
The data is backed up automatically every night (14 days are kept) in
    C:\ProgramData\CafeManager\backups
Once a week, copy the newest file from that folder to a USB stick.


UPDATING TO A NEW VERSION
-------------------------
Extract the new package and double-click "Install Cafe Manager Server" again.
Your data is kept.


REMOVING
--------
Double-click "Uninstall Cafe Manager Server". Your data is kept unless you
choose to delete it.


SOMETHING WRONG?
----------------
- Other devices cannot open the address: on this PC go to Windows Settings >
  Network & Internet > your network, and set it to "Private".
- The page does not open at all: restart this PC and wait two minutes.
- Technical details: docs\INSTALL.md
