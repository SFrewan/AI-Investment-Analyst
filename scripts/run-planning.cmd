@echo off
setlocal
rem PRICE ACQUISITION PLANNING - ZERO PROVIDER CALLS.
rem   No connector is switched on, so no SEC or EODHD request can leave the machine.
rem   NO prices. NO splits. NO dividends. NO corporate actions. NO scoring.
rem   NO strategy declarations. NO orders. NO threshold changed.
rem   The remaining SEC call stays unspent.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  PRICE ACQUISITION PLANNING - us-pit-sample400 @ f78cfe45b962
echo   1. Release build, warnings are errors
echo   2. planning invariants: 269 ready, 131 excluded, 400 members
echo   3. identity rules
echo   4. full Release suite
echo   ZERO provider calls. Design only.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\planning-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\planning-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-planning.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Plan: artifacts\verify\price-acquisition-plan.md
echo.
pause
endlocal
