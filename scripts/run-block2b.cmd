@echo off
setlocal
rem BLOCK 2B - REGRESSION GATE. Build + focused + full Release suite.
rem   AIINV_BACKFILL is NOT set here, so the backfill test skips and no EODHD call is made.

cd /d "%~dp0.."
set AIINV_BACKFILL=

echo.
echo ===============================================================
echo  BLOCK 2B - REGRESSION GATE AFTER THE BACKFILL
echo   Release build with warnings-as-errors, focused tests,
echo   then the full suite.
echo   No EODHD request. No cycle. No cooldown wait.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-block2b.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
