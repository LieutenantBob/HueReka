@echo off
setlocal DisableDelayedExpansion
if not exist "%~dp0dist\HueReka!.exe" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if not exist "%~dp0dist\HueReka!.exe" (pause & exit /b 1)
start "" "%~dp0dist\HueReka!.exe"
