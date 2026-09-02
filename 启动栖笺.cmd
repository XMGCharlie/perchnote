@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist "dist\PerchNote.exe" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
  if errorlevel 1 pause & exit /b 1
)
start "" "%~dp0dist\PerchNote.exe"
