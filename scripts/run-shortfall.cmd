@echo off
setlocal
rem THE ONE-UNIT SPLIT SHORTFALL: RECONCILIATION AND DRAFT RESOLUTION. READ-ONLY.
rem   ZERO EODHD calls. ZERO SEC calls. No price, split or dividend acquisition.
rem   No acquisition switch is set. NO SPLIT BATCH RUNS and NONE IS AUTHORISED.
rem   Nothing is written to the store; no quarantine is reprocessed or reclassified.
rem   The sealed manifest and BOTH authorisation declarations are read, never modified.
rem   The drafted successor authorisation goes to artifacts\verify\ ONLY - it is NOT
rem   installed into declarations\, so nothing can load it or spend against it.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  SPLIT SHORTFALL PREPARATION - NO DISPATCH, NO INSTALL
echo   1. Release build, warnings are errors
echo   2. establish the shortfall, draft the successor, do not install
echo   3. every standing invariant
echo   4. full Release suite
echo   NOTHING IS ACQUIRED. NO SPLIT BATCH IS AUTHORISED OR RUN.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\shortfall-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\shortfall-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-shortfall.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
