@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -RunAfterBuild
if errorlevel 1 (
  echo.
  echo Build failed. Please send build.log to Codex.
  pause
)
