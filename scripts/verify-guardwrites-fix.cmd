@echo off
setlocal
rem VERIFY THE GUARDWRITES OWNED-ENTITY FIX.
rem Builds Release, runs WriteGuardTests, runs the full suite, builds the API.
rem Does NOT start the API, does NOT change RunCycles, makes NO EODHD request,
rem and never writes to the ai_investment development database.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  GUARDWRITES OWNED-ENTITY FIX - BUILD AND TEST
echo.
echo  1. dotnet build   (Release, whole solution)
echo  2. WriteGuardTests only
echo  3. the full suite
echo  4. dotnet build   (Release, the API)
echo.
echo  A transcript is written to artifacts\verify\guardwrites-fix.txt
echo  Nothing is started. No EODHD request is made.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-guardwrites-fix.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
