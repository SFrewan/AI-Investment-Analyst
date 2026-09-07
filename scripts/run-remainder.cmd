@echo off
setlocal
rem INSTALL THE SPLIT REMAINDER AUTHORISATION. NO DISPATCH, NO BATCH AUTHORISED.
rem   ZERO EODHD calls. ZERO SEC calls. No price, split or dividend acquisition.
rem   No acquisition switch is set. NO SPLIT BATCH RUNS and NONE IS AUTHORISED.
rem   Nothing is written to the store; no quarantine is reprocessed or reclassified.
rem   The sealed manifest and BOTH authorisation declarations are read, never modified.
rem   The successor authorisation IS installed into declarations\. That is a ceiling,
rem   not a licence: EVERY split batch stays unauthorised and nothing dispatches.
rem   The predecessor and price authorisations are read and left byte-identical.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  SPLIT REMAINDER AUTHORISATION - INSTALL ONLY, NO DISPATCH
echo   1. Release build, warnings are errors
echo   2. install the successor, re-cut partition checked, nothing authorised
echo   3. quarantine survey, read-only, source attribution corrected
echo   4. every standing invariant
echo   5. full Release suite
echo   NOTHING IS ACQUIRED. NO SPLIT BATCH IS AUTHORISED OR RUN.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\remainder-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\remainder-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-remainder.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
