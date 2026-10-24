@echo off
rem Faceit Tunnel: runs faceit-tunnel.ps1 as administrator (TUN needs admin rights).
net session >nul 2>&1
if %errorlevel% neq 0 (
    if "%~1"=="" (
        powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    ) else (
        powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs"
    )
    exit /b
)
cd /d "%~dp0"
title Faceit Tunnel
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0faceit-tunnel.ps1" %*
echo.
pause
