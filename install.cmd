@echo off
rem Installs Survey to Revit for the current Windows user only. No admin rights needed.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
pause
