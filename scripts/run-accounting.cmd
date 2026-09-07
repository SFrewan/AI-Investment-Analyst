@echo off
setlocal
rem SCOPE PRIOR CONSUMPTION TO THE ACTIVE AUTHORISATION. BUILD AND TEST ONLY.
rem   ZERO EODHD calls. ZERO SEC calls. No price, split or dividend acquisition.
rem   No acquisition switch is set. NO SPLIT BATCH RUNS and NONE IS AUTHORISED.
rem   Nothing is written to the store; no quarantine is reprocessed or reclassified.
rem   The sealed manifest and BOTH authorisation declarations are read, never modified.
rem   NOTHING IS INSTALLED, AMENDED OR MIGRATED. Historical artefacts are read as
rem   recorded and asserted unchanged. EVERY split batch stays unauthorised.
rem   GPC.US IS NOT EXECUTED.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  SPLIT ACCOUNTING FIX - BUILD AND TEST ONLY, NO DISPATCH
echo   1. Release build, warnings are errors
echo   2. live accounting scoped to the active authorisation, read-only
echo   3. every standing invariant, incl. the new prior-consumption rules
echo   4. full Release suite
echo   NOTHING IS ACQUIRED. NO SPLIT BATCH IS AUTHORISED OR RUN.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\accounting-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\accounting-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-accounting.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
