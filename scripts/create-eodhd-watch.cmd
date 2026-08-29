@echo off
setlocal
rem OBSERVATION WINDOW - STEP 5: CREATE THE FIRST MARKET-DATA WATCH.
rem You will be shown exactly what changes, asked to type CREATE, then prompted for the
rem operator key. Input is hidden and is never written to a file.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  OBSERVATION WINDOW - STEP 5: CREATE ONE WATCH
echo.
echo  POST http://localhost:5143/api/operator/watches
echo    Security / AAPL.US / equity-price-review
echo    every 1440 min, cooldown 240 min, OpportunityManagement
echo.
echo  No EODHD request. No cycle. No RunCycles change. No trade.
echo.
echo  Reversible: POST /api/operator/watches/{id}/disablement
echo  switches it off through the same seam. RunCycles is also
echo  false, which the script verifies before asking.
echo.
echo  Close this window now if you do not want to proceed.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0create-eodhd-watch.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
