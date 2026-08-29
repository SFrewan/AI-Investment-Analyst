@echo off
setlocal
rem SELF-LIMITING LIVE OBSERVATION RUN.
rem   Waits out the watch cooldown, starts the API with RunCycles=true on 5143,
rem   observes the scheduled cycle, STOPS THE API ITSELF, then verifies from the database.
rem   Leave this window open. It closes the API on its own.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  LIVE OBSERVATION - SELF LIMITING
echo.
echo  1. wait until the AAPL.US watch is out of cooldown
echo  2. start AI.Investment.Api.exe  (Release, Development, :5143)
echo     with OperationsHost__RunCycles=true
echo  3. observe the scheduled cycle
echo  4. STOP the API automatically
echo  5. verify cycle / ingestion / audit / EODHD from the database
echo.
echo  This WILL make one real EODHD request.
echo  Nothing is left running: the script stops the API itself.
echo  Transcript: artifacts\verify\live-observation.txt
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0observe-live-cycles.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
