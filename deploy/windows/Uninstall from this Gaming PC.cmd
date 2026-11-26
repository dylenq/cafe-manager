@echo off
setlocal
title Cafe Manager - remove from gaming PC
rem Double-click this file. It runs the PowerShell script next to it; Windows asks for administrator rights (choose Yes).
rem The window stays open when something goes wrong, so the error can be read.
if not exist "%~dp0uninstall-agent.ps1" (
  echo.
  echo ERROR: uninstall-agent.ps1 was not found next to this file.
  echo Extract the whole zip first: right-click it, choose Extract All, then run this file
  echo from the extracted folder. It does not work when opened from inside the zip.
  echo.
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall-agent.ps1"
set CAFE_EXIT=%ERRORLEVEL%
if not "%CAFE_EXIT%"=="0" (
  echo.
  echo The script stopped with exit code %CAFE_EXIT%. Read the message above, or take a photo of this window.
  pause
)
exit /b %CAFE_EXIT%
