@echo off
setlocal
rem POST-BATCH-1 PLANNING AND INTEGRITY REVIEW. READ-ONLY.
rem   ZERO EODHD calls. ZERO SEC calls. No price, split or dividend acquisition.
rem   No acquisition switch is set. Batch 2 is NOT run and NOT marked authorised.
rem   Nothing is written to the store; no quarantine is reprocessed or reclassified.
rem   The authorisation, the sealed manifest and the identity table are read, never modified.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  POST-BATCH-1 REVIEW - READ-ONLY
echo   1. Release build, warnings are errors
echo   2. the review itself, connectors off
echo   3. every standing invariant
echo   4. full Release suite
echo   NOTHING IS ACQUIRED. BATCH 2 DOES NOT RUN.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\review-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\review-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-review.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
