@echo off
setlocal
rem TRANSPORT / PROCESS-START FORENSICS. READ-ONLY. NO DISPATCH.
rem   ZERO EODHD calls. ZERO SEC calls. No price, split or dividend acquisition.
rem   No acquisition switch is set. NO SPLIT BATCH RUNS and NONE IS AUTHORISED.
rem   Nothing is written to the store; no quarantine is reprocessed or reclassified.
rem   The sealed manifest and BOTH authorisation declarations are read, never modified.
rem   NOTHING IS DISPATCHED, RETRIED, RECLASSIFIED OR REPAIRED. The ledger and the
rem   batch artefacts are read as recorded. NO authorisation is created or changed.
rem   BATCH 3 IS NOT RUN. MYO.US IS NOT RETRIED.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  TRANSPORT FORENSICS - READ-ONLY, NO DISPATCH
echo   1. Release build, warnings are errors
echo   2. transport failure forensics over the ledger and artefacts
echo   3. every standing invariant, incl. the new prior-consumption rules
echo   4. full Release suite
echo   NOTHING IS ACQUIRED. NO SPLIT BATCH IS AUTHORISED OR RUN.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\forensics2-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\forensics2-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-forensics2.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
