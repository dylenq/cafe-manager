@echo off
title Cafe Manager - install server
rem Double-click this file. It runs the PowerShell installer next to it; Windows asks for administrator rights (choose Yes).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-server.ps1"
