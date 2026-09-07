@echo off
setlocal
rem SCREEN THE ELEVEN RECOVERED PRICE SERIES THROUGH SplitAdjustment - READ-ONLY DIAGNOSTIC.
rem   ZERO EODHD calls. ZERO SEC calls. No price, split or dividend acquisition.
rem   No acquisition switch is set. NO SPLIT BATCH RUNS and NONE IS AUTHORISED.
rem   Nothing is written to the store; no quarantined payload is released, reclassified
rem   or removed. The normaliser and SplitAdjustment are called directly, in memory, and
rem   their results discarded. No row is repaired and no gap is filled or interpolated.
rem   Gate 6, its sealed declaration, CoverageEvaluation, the normaliser, the pipeline
rem   and SplitAdjustment are NOT modified.
rem   One report is written: artifacts\verify\gate6-recovered-series-move-screen.md

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  RECOVERED SERIES MOVE SCREEN - READ-ONLY, NO ACQUISITION
echo   1. Release build, warnings are errors
echo   2. screen the 11 recovered series through SplitAdjustment
echo   3. execute Gate 6 against the hypothetical, in memory
echo   4. full Release suite
echo   NOTHING IS ACQUIRED. NOTHING IS RELEASED FROM QUARANTINE.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\movescreen-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\movescreen-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-movescreen.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
