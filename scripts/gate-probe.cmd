@echo off
setlocal
rem GATE 2 - SUBSCRIPTION DEPTH PROBE.
rem   TWO REAL, BILLABLE CALLS: the account endpoint, and one month inside the second year.
rem   No name, email or token is written to the report.

cd /d "%~dp0.."
set AIINV_PROBE=1

echo.
echo ===============================================================
echo  GATE 2 - SUBSCRIPTION DEPTH PROBE
echo   Asks the vendor whether this account is entitled to the
echo   second year of history that the backfill did not receive.
echo   THIS MAKES TWO REAL, BILLABLE CALLS.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~SubscriptionDepthProbeTests" -LogName "gate-probe.log" -Label "gate 2: subscription depth probe"
set EXITCODE=%ERRORLEVEL%

set AIINV_PROBE=
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
