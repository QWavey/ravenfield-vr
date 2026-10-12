@echo off
rem Double-click to remove Ravenfield VR and restore the original files.
if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" -GameDir "%~1"
)
pause
