@echo off
rem Faceit Tunnel without VPN: Faceit goes direct from your own IP, TLS fragmentation bypasses DPI.
call "%~dp0start.bat" -Mode direct
