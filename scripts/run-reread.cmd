@echo off
setlocal
rem REPLAY ARCHIVED PRICE PAYLOADS THROUGH THE REAL NORMALISER - READ-ONLY DIAGNOSTIC.
rem   ZERO EODHD calls. ZERO SEC calls. No price, split or dividend acquisition.
rem   No acquisition switch is set. NO SPLIT BATCH RUNS and NONE IS AUTHORISED.
rem   Nothing is written to the store; no quarantined payload is released, reclassified
rem   or removed. The normaliser is called directly, in memory, and its results discarded.
rem   Gate 6, its sealed declaration, the normaliser and the pipeline are NOT modified.
rem   One report is written: artifacts\verify\gate6-price-payload-reread.md

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  PRICE PAYLOAD RE-READ - READ-ONLY, NO ACQUISITION
echo   1. Release build, warnings are errors
echo   2. replay every archived price payload through the normaliser
echo   3. full Release suite
echo   NOTHING IS ACQUIRED. NOTHING IS RELEASED FROM QUARANTINE.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\reread-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\reread-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-reread.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
