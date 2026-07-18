@echo off
taskkill /F /IM Shortcutz.exe >nul 2>&1
start bin/Debug/net10.0-windows/Shortcutz.exe
