# Cafe Manager: Install and Operations Guide

This guide is for the cafe owner or whoever sets up the computers. It takes you from the release zip to a working cafe: server, first-run setup, gaming PCs, Samsung TVs, receipt printer, backups, upgrades and removal. Day-to-day use is covered in `USER-GUIDE.md`.

Only run the scripts from the bundle you built yourself or received directly from the person who built it. The programs are not code-signed in version 1.0.0.

## What you need

- A Windows 10 or 11 PC at the front desk to act as the server. It must stay on whenever the cafe is open. Give it a fixed address on your network: in your router, find the DHCP reservation (or "address reservation") page, pick this PC and reserve its current address. Without this the address can change after a reboot and every gaming PC would lose the server.
- The release zip, `CafeManager-1.0.0-win-x64.zip`, copied to the server PC (a USB stick is fine).
- Administrator rights on the server PC and on every gaming PC.
- The gaming PCs on the same local network (same router) as the server. No internet connection is needed to run the cafe.
- Samsung smart TVs on the same network for console stations.

Preparing the zip:

1. Right-click the zip, choose Properties, tick Unblock (if shown), press OK. Then extract it, for example to `C:\CafeManager-Setup`. If you already extracted it, open PowerShell in that folder and run `Get-ChildItem -Recurse . | Unblock-File`.
2. The programs are unsigned, so Windows SmartScreen may show "Windows protected your PC". Choose More info, then Run anyway.
3. Run each script from PowerShell (or a Command Prompt) opened in the extracted folder, using the form below. Each script asks for administrator rights itself and shows a Windows (UAC) prompt: choose Yes.

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-server.ps1
```

## 1. Install the server on the front-desk PC

Run `install-server.ps1` as shown above. It does the following:

1. Copies the server to `C:\Program Files\CafeManager\Server`.
2. Keeps all data in `C:\ProgramData\CafeManager`, with access limited to SYSTEM and Administrators (the database contains the admin password hash and the agent key). Logs go to `C:\ProgramData\CafeManager\logs`, backups to `C:\ProgramData\CafeManager\backups`.
3. Creates the Windows service `CafeManager` ("Cafe Manager Server") with automatic delayed start, and restart on failure (after 5 seconds, 5 seconds, then 30 seconds).
4. Opens the Windows Firewall for TCP port 5080 with the rule "Cafe Manager (TCP 5080)", for Private and Domain networks only.
5. Starts the service, waits up to 40 seconds for it to answer, prints the addresses of the server on your network, and opens `http://localhost:5080` in the browser.

Optional parameters: `-SourcePath` (folder holding `Cafe.Server.exe`, default is the `Server` folder next to the script), `-InstallPath`, `-NoBrowser`, `-NoPause`.

If the PC's network is classified as Public, other devices cannot reach the server and the script prints a warning. Fix it with Settings, Network & Internet, your network, Network profile type, Private. Then try the address from a phone again.

Confirm it works: press Win+R, type `services.msc`, find "Cafe Manager Server" and check that the status is Running (startup type "Automatic (Delayed Start)").

## 2. First-run setup

First-run setup must be done on the server PC itself, at `http://localhost:5080`. Devices elsewhere on the network are shown an explanation page instead, so nobody else can claim the admin password first.

1. On the server PC open `http://localhost:5080`. The setup page appears automatically.
2. Choose the shared admin password (at least 8 characters, entered twice). Store it safely. There is no password-reset page; the password is stored only as a hash.
3. Set the time zone and currency format. These are server settings, not screens in the app:
   - `Cafe:TimeZone`: the cafe's time zone, for example `Asia/Ho_Chi_Minh` or `Pacific Standard Time`. It decides when a "day" starts for rate rules and reports. If empty or unknown, the server PC's own time zone is used.
   - `Cafe:Culture`: a culture name such as `en-US` or `vi-VN`. It decides how amounts and dates are formatted. If empty, the server's own culture is used.

   The most durable way is machine environment variables, because they survive upgrades. In an administrator PowerShell:

   ```
   [Environment]::SetEnvironmentVariable('Cafe__TimeZone', 'Asia/Ho_Chi_Minh', 'Machine')
   [Environment]::SetEnvironmentVariable('Cafe__Culture', 'vi-VN', 'Machine')
   Restart-Service CafeManager
   ```

   (Use your own values.) Alternatively edit `appsettings.json` in `C:\Program Files\CafeManager\Server`, but upgrading replaces that folder, so you would have to repeat the edit.
4. Log in and create station types (Station types page: name, base hourly rate, optional member rate), then stations (Stations page: Add station).
   - For a PC station choose Kind PC and fill in "Agent machine ID" (see section 4).
   - For a console station choose Kind Console and fill in "TV IP address" and "TV MAC address" (see section 5).

After setup, the front-desk tablet or any phone can use the address from section 3.

## 3. Find the server address on your network

1. The installer prints the addresses at the end, for example `http://192.168.1.10:5080`.
2. To see them again, open a Command Prompt on the server PC and run `ipconfig`. Use the IPv4 Address of the network connection that is on the cafe network.
3. Open that address in the browser of the front-desk tablet or any phone or PC on the cafe network, log in with the admin password, and bookmark it.

The connection uses plain HTTP and no TLS, so keep the cafe network private (no guest Wi-Fi on the same network as the server).

## 4. Install the agent on each gaming PC

The agent keeps the PC locked until a session is started and locks it again when the time runs out.

1. Copy the extracted bundle (or at least `install-agent.ps1` and the `Agent` folder) to the gaming PC.
2. On the server open Settings, find the PC agents card, and use the copy button next to the shared key. Do not share this key outside the cafe.
3. Choose an emergency unlock code of at least 6 characters. Staff type it on the lock screen if the server is unreachable.
4. On the gaming PC, in the extracted folder, run (replace the placeholders with your own values):

   ```
   powershell -NoProfile -ExecutionPolicy Bypass -File .\install-agent.ps1 -ServerUrl http://192.168.1.10:5080 -SharedKey <shared-key-from-settings> -EmergencyCode <your-emergency-code> -MachineId PC-01 -GamingUser gamer
   ```

   | Parameter | Required | Meaning |
   |-----------|----------|---------|
   | `-ServerUrl` | yes | Address of the server (section 3). |
   | `-SharedKey` | yes | The key from Settings, PC agents card. |
   | `-EmergencyCode` | yes | Emergency unlock code, 6 or more characters. Only a hash is stored on the PC. |
   | `-MachineId` | no | Name the server shows for this PC. If omitted, the Windows computer name is used. |
   | `-GamingUser` | no | The Windows account customers play on, as `user` or `DOMAIN\user`. Default: the account running the script. |
   | `-SourcePath`, `-InstallPath`, `-NoPause` | no | Source folder (default: `Agent` next to the script), install folder (default `C:\Program Files\CafeManager\Agent`), skip the final "press Enter". |

   The values you type are visible in the process list while the installer runs. This is accepted for a one-time admin action.
5. What the installer sets up: the agent in `C:\Program Files\CafeManager\Agent`; `agentsettings.json` (read-only for the gaming user) and a writable logs folder; a scheduled task "CafeManager Agent" that starts the agent when the gaming user logs on, with highest privileges, restarting every minute if it stops; and Task Manager disabled for the gaming user so customers cannot close the agent. If the gaming user is logged on, the agent starts immediately.
6. Use a standard (non-administrator) Windows account as the gaming user. An account that is an administrator can undo these restrictions.
7. Make the station match: on the server, the station's "Agent machine ID" must equal the machine id of the PC (case does not matter). Either create the station with that id, or, once the agent has connected, pick the PC from the "Unregistered agents" list in Settings and assign it to a station.
8. Test it: log the gaming user out and in. The PC should come up locked and the station should show as connected. Start a 1-minute session from the dashboard and confirm the PC unlocks and locks again.

Notes:

- The agent runs only while the gaming user is logged on. If that user is logged out, nothing is running on the PC; it simply sits at the Windows sign-in screen.
- If the folder `Agent` in the bundle contains a file `FRAMEWORK-DEPENDENT.txt`, the agent was built without the .NET runtime and each gaming PC needs the .NET 10 Desktop Runtime installed first. If that file is not there, the agent carries its own runtime.
- Emergency unlock: on the lock screen press `Ctrl+Alt+Shift+F12` and enter the emergency code. After 3 wrong codes the prompt is locked for 30 seconds, and the lockout doubles with further failures (up to 15 minutes). A correct code unlocks the PC until the next message from the server.
- Remove the agent with `uninstall-agent.ps1` (section 9).

## 5. Samsung TV setup

Console stations are controlled by switching the connected Samsung Tizen TV on and off over the network.

On each TV (menu names vary slightly by model year):

1. Settings, General, Network, Expert Settings: turn on "Power On with Mobile".
2. In the same place, turn on "IP Remote".
3. Turn on network standby (so the TV answers Wake-on-LAN while it is off). A model without network standby may show "TV Unreachable" while it is off.
4. Give the TV a fixed IP address, either by a router DHCP reservation (as for the server) or in the TV's network settings. Note its IP address and its MAC address (TV network status screen).

In Cafe Manager:

1. Go to the Stations page, edit (or add) the console station and fill in "TV IP address" (for example `192.168.1.50`) and "TV MAC address" (for example `AA:BB:CC:DD:EE:FF`). Save.
2. In the station's TV controls press "Pair TV". Look at the TV screen and accept the connection request. The badge changes from "Not paired" to "Paired". Use "Re-pair TV" if you reset the TV later.
3. Press "Test" to check that the server can reach the TV. The status indicator on the dashboard shows "TV On", "TV Standby", "TV Unreachable", "TV Unknown" or "TV not checked". The "TV On" and "TV Off" buttons switch the TV by hand.
4. In Settings, the Samsung TVs card has the switch "Power off the TV when a session is stopped" (on by default). Press Save after changing it. When it is on, the TV is switched off when a session ends or the paid time (plus grace) runs out.

## 6. Receipt printer setup

Receipts are printed from the receipt printer settings in Settings, "Receipts" card. Choose one "Print mode":

- Browser: the receipt opens in the browser and the normal print dialog appears. Install the printer as a normal Windows printer, choose it, and in the print dialog set the paper size to your roll width, margins to None, and turn off "Headers and footers". This works with any printer.
- Network printer: for a thermal printer with a network connection that accepts ESC/POS. Fill in "Printer host or IP" and "Port" (usually 9100) and choose "Paper width": 32 columns for 58 mm paper, 42 columns for 80 mm paper. If the network printer cannot be reached, the server falls back to the Windows printer named below.
- Windows printer: for a thermal printer installed in Windows on the server PC. Fill in "Windows printer name" exactly as shown in Windows, and the same "Paper width". The receipt is sent as raw ESC/POS, so the printer must understand ESC/POS (most receipt printers do).

Also fill in the receipt text: "Cafe name", "Phone", "Address" and "Footer". Press Save, then "Print test receipt" to check the result. (In Browser mode the test says that the real print happens from the receipt page after a payment.) During sales, a receipt can be printed again from the "Recent receipts" list on the POS page ("Reprint").

## 7. Backups and restore

- The server backs up the database automatically every night at 04:00 (server local time). Change the hour with `Backup:Hour` (0 to 23).
- The newest 14 backups are kept; older ones are deleted. Change with `Backup:Keep`.
- Backups are in `C:\ProgramData\CafeManager\backups` unless `Backup:Directory` is set.
- Settings has a Backups card showing "Last backup", the backup folder, and the retention. Press "Back up now" for an extra copy (for example before an upgrade).
- Copy the newest backup to a USB stick or another PC at least once a week. A backup on the same disk does not help if the disk dies.

To restore:

1. Stop the service: `Stop-Service CafeManager` in an administrator PowerShell (or stop it in `services.msc`).
2. In `C:\ProgramData\CafeManager`, rename the current `cafe.db` (for example to `cafe.db.broken`). Delete any `cafe.db-wal` and `cafe.db-shm` files next to it only after you have renamed `cafe.db`. Keep the old files until you are sure the restore is good.
3. Copy the backup file into the folder and name the copy `cafe.db`.
4. Start the service: `Start-Service CafeManager`. Log in and check the data.

## 8. Upgrading

1. Make an extra backup (Settings, "Back up now").
2. Extract the new bundle to a new folder.
3. On the server PC run `install-server.ps1` from the new folder. It stops the service, replaces the program files and starts it again. Everything in `C:\ProgramData\CafeManager` is left alone, and the database updates itself at startup. Settings stored in machine environment variables are kept; values you edited inside `appsettings.json` in the install folder are replaced.
4. On each gaming PC run `install-agent.ps1` from the new bundle with the same parameters as before. It is safe to re-run.

## 9. Uninstalling

- Server: `uninstall-server.ps1`. It removes the service, the firewall rule and the program files and keeps your data. Add `-RemoveData` to delete `C:\ProgramData\CafeManager` as well: this deletes the database and all backups, cannot be undone, and asks you to type DELETE first (copy the backups somewhere first). Other parameters: `-Force` (skip the DELETE question), `-InstallPath`, `-NoPause`.
- Gaming PC: `uninstall-agent.ps1`. It removes the scheduled task, the program files and the Task Manager restriction. Parameters: `-GamingUser` (the account to restore, default the user running the script), `-InstallPath`, `-RemoveData` (also deletes the agent's saved lockout state in `C:\ProgramData\CafeManager\agent`), `-NoPause`. Run it for each PC.

## 10. Configuration reference

Settings can be given in `appsettings.json` (in the server's install folder) or as environment variables. For a service use machine-level environment variables and restart the service afterwards (`Restart-Service CafeManager`). In an environment variable a colon becomes two underscores.

| Setting | Default | Environment variable | Meaning |
|---------|---------|----------------------|---------|
| `Kestrel:Endpoints:Http:Url` | `http://0.0.0.0:5080` | `Kestrel__Endpoints__Http__Url` | Address and port the server listens on. If you change the port, also change the firewall rule. |
| `ConnectionStrings:Cafe` | empty (see below) | `ConnectionStrings__Cafe` | Database connection string. When set it always wins. |
| `Cafe:DataDirectory` | `C:\ProgramData\CafeManager` | `Cafe__DataDirectory` | Where the database, logs and backups live when running as a service or in Production. |
| `Cafe:Culture` | empty (server culture) | `Cafe__Culture` | Format of amounts and dates, for example `en-US`, `vi-VN`. |
| `Cafe:TimeZone` | empty (server time zone) | `Cafe__TimeZone` | Time zone for rate rules, reports and displayed times. |
| `Setup:AllowRemote` | `false` | `Setup__AllowRemote` | Allow first-run setup from another device. Anyone on the network could then claim the password, so set it back to `false` straight after. |
| `Backup:Hour` | `4` | `Backup__Hour` | Hour (0 to 23) of the nightly backup. |
| `Backup:Keep` | `14` | `Backup__Keep` | Number of backups kept (at least 1). |
| `Backup:Directory` | empty (`backups` folder) | `Backup__Directory` | Folder for backups, for example a second disk. |
| `Backup:RunScheduler` | `true` | `Backup__RunScheduler` | Turn the nightly backup off with `false`. |
| `Sessions:RunTicker` | `true` | `Sessions__RunTicker` | Background job that expires sessions. Leave on; for tests only. |
| `Tv:RunMonitor` | `true` | `Tv__RunMonitor` | Background job that checks TV status. Leave on; for tests only. |

With no connection string, a service (or Production run) stores the database at `C:\ProgramData\CafeManager\cafe.db`; a development run uses `cafe.db` in the project folder. Grace period, warning minutes and the TV power-off switch are changed in the app under Settings, not here. Receipt and printer options are also in Settings.

## 11. Troubleshooting installs

- The service will not start, or the installer says the server did not answer: open `C:\ProgramData\CafeManager\logs` and read the newest log file. A common cause is another program already using port 5080: run `netstat -ano | findstr :5080` in a Command Prompt, or change `Kestrel__Endpoints__Http__Url` to another port (and the firewall rule).
- A phone or another PC cannot connect: check the server address (`ipconfig`), that the server PC's network profile is Private, that the firewall rule "Cafe Manager (TCP 5080)" exists and is enabled, and that the device is on the same network. Try `http://<server-ip>:5080` in the server PC's own browser first.
- The script closes at once or asks for rights: the UAC prompt was not accepted. Run it again and choose Yes. Run it from a PowerShell window opened as administrator if the prompt does not appear.
- "Running scripts is disabled" or "not digitally signed": use the full command with `-ExecutionPolicy Bypass` shown at the top of this guide, and unblock the zip (see "What you need").
- Agent shows offline on the dashboard: check `ServerUrl` in `C:\Program Files\CafeManager\Agent\agentsettings.json` (the installer wrote it), that the PC can open the server address in a browser, that the gaming user is logged on, and that the station's "Agent machine ID" matches. A 401 in the agent log (`logs` folder next to the agent) means the shared key is wrong or was regenerated on the server: re-run `install-agent.ps1` with the new key.
- The lock screen says "Agent configuration error": `agentsettings.json` is missing or invalid. Re-run `install-agent.ps1`.
- The installer warns that a temporary registry hive `HKU\...` could not be unloaded: restart the PC, or run the `reg.exe unload` command shown in the warning.
- The installer warns that the gaming user has no profile yet, so Task Manager was not disabled: log that user in once, then run `install-agent.ps1` again.
