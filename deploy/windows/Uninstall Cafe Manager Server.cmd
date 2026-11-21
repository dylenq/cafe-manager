@echo off
title Cafe Manager - remove server
rem Double-click this file. It runs the PowerShell installer next to it; Windows asks for administrator rights (choose Yes).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall-server.ps1"
