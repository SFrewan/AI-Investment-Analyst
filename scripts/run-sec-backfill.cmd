@echo off
setlocal
rem THE SEC EDGAR FUNDAMENTALS BACKFILL.
rem   Real requests to a public-domain U.S. government service. Free, rate-limited.
rem   No opportunity, no prediction, no trade, no cycle.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-sec-backfill.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
