@echo off
setlocal
rem HOLD EVERY REFUSED SESSION PAIR AGAINST LOCAL DATED EVIDENCE - READ-ONLY DIAGNOSTIC.
rem   ZERO EODHD calls. ZERO SEC calls. ZERO network. Local evidence only.
rem   No acquisition switch is set. NO SPLIT BATCH RUNS and NONE IS AUTHORISED.
rem   Nothing is written to the store; no quarantined payload is released, reclassified
rem   or removed. The normaliser and SplitAdjustment are called directly, in memory, and
rem   their results discarded. No price is repaired, interpolated or synthesised.
rem   Gate 6, its sealed declaration, CoverageEvaluation, the normaliser, the pipeline
rem   and SplitAdjustment are NOT modified.
rem   One report is written: artifacts\verify\gate6-local-coincidence-check.md

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  LOCAL COINCIDENCE CHECK - READ-ONLY, LOCAL-ONLY, NO ACQUISITION
echo   1. Release build, warnings are errors
echo   2. hold all 23 breaches against every dated local record
echo   3. full Release suite
echo   NOTHING IS ACQUIRED. NOTHING IS RELEASED FROM QUARANTINE.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\coincidence-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\coincidence-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-coincidence.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
