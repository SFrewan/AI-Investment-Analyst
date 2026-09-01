@echo off
setlocal
rem POST-FIX LIVE END-TO-END VERIFICATION.
rem   Starts the API with OperationsHost__RunCycles=true, observes the scheduled AAPL cycle
rem   through every stage, proves the cooldown suppresses the ticks that follow,
rem   STOPS THE API ITSELF, then verifies everything from the database.
rem   Leave this window open. It closes the API on its own.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  POST-FIX LIVE END-TO-END VERIFICATION
echo.
echo  The 4-hour cooldown has already elapsed - no artificial wait.
echo.
echo  1. baseline counts
echo  2. start AI.Investment.Api.exe (Release, Development, :5143)
echo     with OperationsHost__RunCycles=true
echo  3. observe the cycle through all 14 stages
echo  4. observe the cooldown suppressing later ticks
echo  5. STOP the API automatically
echo  6. verify cycle / ingestion / owned graph / audit / outbox
echo.
echo  This WILL make a real EODHD request.
echo  Nothing is left running.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0live-verify.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
