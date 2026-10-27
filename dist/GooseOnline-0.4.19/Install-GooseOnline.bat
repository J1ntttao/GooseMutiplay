@echo off
title Goose Online One-click Install
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 (
  echo.
  echo Installation failed. Send the error above to the mod author.
  pause
)
