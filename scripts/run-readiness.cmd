@echo off
setlocal
rem IDENTITY REPORT REGENERATION - ZERO PROVIDER CALLS.
rem   The EDGAR connector is NOT switched on, so no SEC request can leave the machine.
rem   The retry of the unresolved CIK is off; the final authorised SEC call stays unspent.
rem   NO EODHD calls. NO price acquisition. NO corporate actions. NO scoring.
rem   NO strategy declarations. NO orders. NO threshold changed.
rem   The sealed manifest is hashed before and after and the run fails if the hash moves.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  IDENTITY REPORT REGENERATION - us-pit-sample400 @ f78cfe45b962
echo   1. Release build, warnings are errors
echo   2. source label = what supplied the symbol, not what was queried
echo   3. conflict table = genuine conflicts only, ambiguous excluded
echo   4. idempotency pre-check, then the 12 gates
echo   5. focused identity tests, then the full Release suite
echo   ZERO provider calls. Connector off. Retry off.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\readiness-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\readiness-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-readiness.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Identity table: artifacts\universe\identity-sample400.json
echo Report:         artifacts\verify\universe-identity-final.md
echo.
pause
endlocal
