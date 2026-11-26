@echo off
setlocal
title Cafe Manager - install server
rem Double-click this file. It runs the PowerShell script next to it; Windows asks for administrator rights (choose Yes).
rem This window always waits for a key at the end, so every message can be read.
if not exist "%~dp0install-server.ps1" (
  echo.
  echo ERROR: install-server.ps1 was not found next to this file.
  echo Extract the whole zip first: right-click it, choose Extract All, then run this file
  echo from the extracted folder. It does not work when opened from inside the zip.
  echo.
  pause
  exit /b 1
)
if not exist "%~dp0Server\Cafe.Server.exe" (
  echo.
  echo ERROR: Server\Cafe.Server.exe was not found next to this file.
  echo Extract the whole zip first: right-click it, choose Extract All, then run this file
  echo from the extracted folder. It does not work when opened from inside the zip.
  echo.
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-server.ps1" -FromLauncher
set CAFE_EXIT=%ERRORLEVEL%
echo.
if not "%CAFE_EXIT%"=="0" echo The script stopped with exit code %CAFE_EXIT%. Read the messages above, or take a photo of this window.
pause
exit /b %CAFE_EXIT%
