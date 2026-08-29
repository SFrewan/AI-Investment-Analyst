@echo off
setlocal
rem SAFE STOP: disable the AAPL.US watch through the operator API.
rem Does NOT stop the API, does NOT change RunCycles, does NOT delete anything.
cd /d "%~dp0.."
echo.
echo ===============================================================
echo  SAFE STOP - DISABLE THE AAPL.US WATCH
echo.
echo  POST /api/operator/watches/{id}/disablement
echo  Disable, not delete. The row, the reason and the firing
echo  history stay. The ticker stops offering it a signal.
echo.
echo  A cycle already running is NOT cancelled - it completes.
echo  RunCycles is not touched. The API keeps running.
echo.
echo  You will be asked for a reason, then to type DISABLE,
echo  then for the operator key.
echo ===============================================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0disable-aapl-watch.ps1"
set EXITCODE=%ERRORLEVEL%
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
