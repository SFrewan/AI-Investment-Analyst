@echo off
setlocal
rem PRE-LIVE VERIFICATION. READ ONLY.
rem Changes no code, configuration or User Secrets. Creates/disables no watch.
rem Does not enable RunCycles, start a cycle, or call EODHD.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  PRE-LIVE VERIFICATION - READ ONLY
echo.
echo  Checks: API health, operator privileges as loaded by the
echo  running API, eodhd-eod active, the AAPL.US watch, RunCycles,
echo  operating_cycles, archive writability, the first schedule
echo  boundary, and that no EODHD request has ever been made.
echo.
echo  You will be asked for the operator key so check 2 can prove
echo  what the RUNNING process loaded. Press Enter to skip it.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pre-live-verification.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
