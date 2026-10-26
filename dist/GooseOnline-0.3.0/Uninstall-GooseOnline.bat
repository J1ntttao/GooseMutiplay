@echo off
title Goose Online One-click Uninstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
if errorlevel 1 (
  echo.
  echo Uninstall failed. Send the error above to the mod author.
  pause
)
