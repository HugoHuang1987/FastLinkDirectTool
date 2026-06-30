@echo off
cd /d "%~dp0"
title Publish FastLinkDirectTool to GitHub
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-github.ps1"
if errorlevel 1 (
  echo.
  echo Upload failed. Keep this window open and send the error to Codex.
  pause
)
