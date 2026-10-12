@echo off
rem Double-click to install Ravenfield VR. Pass a path to install into a specific Ravenfield folder.
if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -GameDir "%~1"
)
pause
