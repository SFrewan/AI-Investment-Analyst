@echo off
setlocal
rem BLOCK 2B - CONTROLLED HISTORICAL BACKFILL
rem   THIS MAKES REAL, BILLABLE PROVIDER CALLS (about 40 on a first run).
rem   Idempotent: a rerun skips whatever the ingestion ledger already records as complete.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  BLOCK 2B - CONTROLLED HISTORICAL BACKFILL
echo.
echo   20 instruments, 2 years of history
echo   corporate actions ingested BEFORE prices, per symbol
echo   idempotent and resumable - a rerun costs only what failed
echo.
echo  THIS MAKES REAL EODHD REQUESTS.
echo  No cycle is started. No cooldown is waited for.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-backfill.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
