@echo off
setlocal
rem OBSERVE THE FIRST CYCLE. READ ONLY. Safe to run repeatedly.
cd /d "%~dp0.."
echo.
echo  Read-only snapshot: API health, watch, cycles, ingestion runs,
echo  observations, opportunities, escalations, audit, archive.
echo  Changes nothing. Starts nothing.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0observe-first-cycle.ps1"
echo.
pause
endlocal
