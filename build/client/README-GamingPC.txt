CAFE MANAGER - GAMING PC
========================

This package goes on every gaming PC. It shows a lock screen whenever no
session is running, unlocks the PC when staff start a session at the desk, and
locks it again when the time is up.

Install the front-desk package FIRST.


BEFORE YOU START - have these ready
-----------------------------------
1. The server address, for example  192.168.1.10
   (shown when you installed the front desk, and in the app under
   Settings > PC agents).
2. The shared key: in the front-desk app go to Settings > PC agents and press
   Copy (or Show and write it down).
3. An emergency unlock code that you make up yourself, at least 6 characters.
   Staff can type it on a locked PC if the front-desk PC is ever off.
   Use the same code on every PC and keep it secret.
4. The Windows account the players use on this PC. It is best if that is a
   normal account (not an administrator), so players cannot undo the lock.
   You also need an administrator password for this PC.


INSTALL (about 1 minute per PC)
-------------------------------
1. Sign in to Windows on the gaming PC as the PLAYER account.
2. Copy this zip to the PC (a USB stick is fine).
3. Right-click the zip > Properties > tick "Unblock" (if you see it) > OK.
4. Right-click the zip > Extract All... > Extract.
5. Open the extracted folder and double-click
       Install on this Gaming PC
6. Answer the questions:
     - Windows account the players use  -> just press Enter
     - Server address                   -> for example 192.168.1.10
     - Shared key                       -> paste it (right-click pastes)
     - Emergency unlock code            -> type it twice
7. Windows asks for permission - click Yes (enter the administrator password
   if asked). If "Windows protected your PC" appears, click "More info",
   then "Run anyway".
8. Wait for "Done." and press Enter. The lock screen appears.

Check it worked: at the front desk the PC now shows on the dashboard (not
"Offline"). Start a short session on it and the lock screen goes away.

Repeat for every gaming PC.


GOOD TO KNOW
------------
- The lock starts by itself every time the player account signs in.
- If the network or the front-desk PC is down, the PC stays locked.
- Emergency unlock: on the locked PC press Ctrl + Alt + Shift + F12 and type
  the emergency code. Only for emergencies - normally unlock from the desk.
- Task Manager is turned off for the player account.


UPDATING TO A NEW VERSION
-------------------------
Extract the new package and run "Install on this Gaming PC" again with the
same answers.


REMOVING
--------
Double-click "Uninstall from this Gaming PC" and press Enter when it asks for
the player account. It removes the lock and turns Task Manager back on.


SOMETHING WRONG?
----------------
- PC shows "Offline" at the desk: check the server address and the shared
  key, check the PC is on the cafe network, then run the installer again.
- The key was changed ("Regenerate" in Settings): run the installer again on
  every PC with the new key.
- Technical details: INSTALL.md in the front-desk package (docs folder).
