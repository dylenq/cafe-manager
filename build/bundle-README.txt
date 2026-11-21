Cafe Manager - release bundle
=============================

Contents
  Server/               The server program (runs on the front-desk PC)
  Agent/                The agent program (runs on every gaming PC)
  docs/                 INSTALL.md and USER-GUIDE.md
  tools/AgentSim-*/     Agent simulator for testing without gaming PCs
  Install Cafe Manager Server.cmd    Double-click on the front-desk PC
  Install on this Gaming PC.cmd      Double-click on each gaming PC (asks 4 questions)
  install-server.ps1    Installs the server as a Windows service
  uninstall-server.ps1  Removes the server (keeps your data unless told otherwise)
  install-agent.ps1     Installs the agent on a gaming PC
  uninstall-agent.ps1   Removes the agent from a gaming PC

The cafe does not need this zip: give them CafeManager-<version>-FrontDesk.zip and
CafeManager-<version>-GamingPC.zip, which contain only what each kind of PC needs and a
plain-language README.txt.

Quick start
  1. Extract this zip (for example to C:\CafeManager-Setup).
  2. On the front-desk PC, run install-server.ps1 as administrator, for example:
       powershell -NoProfile -ExecutionPolicy Bypass -File .\install-server.ps1
     Then finish the setup (admin password) on that PC at http://localhost:5080
  3. On each gaming PC, run install-agent.ps1 with the server address, the shared key
     (Settings page of the admin app) and an emergency code of 6+ characters:
       powershell -NoProfile -ExecutionPolicy Bypass -File .\install-agent.ps1 -ServerUrl http://SERVER-IP:5080 -SharedKey KEY -EmergencyCode CODE

Windows warnings
  The programs are not code-signed in this version. If SmartScreen appears, choose
  "More info", then "Run anyway".
  If Windows blocks the scripts because the zip came from the internet, right-click the
  zip, choose Properties, tick Unblock, press OK, and only then extract it.

More help
  See docs\INSTALL.md for the full install guide and docs\USER-GUIDE.md for daily use.
