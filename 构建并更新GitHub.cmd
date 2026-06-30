@echo off
cd /d "%~dp0"
title Build and update FastLinkDirectTool on GitHub
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -RunAfterBuild
if errorlevel 1 (
  echo.
  echo Build failed. Please send build.log to Codex.
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-github.ps1"
if errorlevel 1 (
  echo.
  echo Upload failed. Please send publish.log to Codex.
  pause
)
