@echo off
rem Faceit Tunnel diagnostics: tests direct, TLS fragmentation and VPN access to Faceit. No admin needed.
cd /d "%~dp0"
title Faceit Tunnel diagnostics
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0diagnose.ps1"
echo.
pause
