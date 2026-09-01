@echo off
setlocal
rem GATE 1 - LEDGER REPAIR.
rem   Finishes the AAPL price ingestion the backfill left half-done, from its ARCHIVED payload.
rem   NO provider call is made. Nothing is deleted. It refuses rather than guesses.

cd /d "%~dp0.."
set AIINV_REPAIR=1

echo.
echo ===============================================================
echo  GATE 1 - LEDGER REPAIR
echo   Records the missing ingestion run from the bytes already
echo   archived, then normalises them.
echo   NO EODHD request. Nothing deleted. Writes to the real database.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~LedgerRepairTests" -LogName "gate-repair.log" -Label "gate 1: ledger repair"
set EXITCODE=%ERRORLEVEL%

set AIINV_REPAIR=
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
