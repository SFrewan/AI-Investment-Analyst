@echo off
setlocal
rem THE AUTHORISED EODHD ACQUISITION.
rem   SPENDS UP TO 704 PROVIDER REQUESTS - the ceiling in the authorisation declaration.
rem   355 authorised symbols, eod then splits, 2021-09-01..2026-08-31.
rem   NO dividends. NO SPY. NO symbol outside the 355. NO widened window. NO SEC calls.
rem   Skipped entirely if artifacts\universe\acquisition-outcome.json already exists.
rem   The sealed universe is neither modified nor resealed.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  AUTHORISED ACQUISITION - us-pit-sample400 @ f78cfe45b962
echo   1. Release build, warnings are errors
echo   2. the authorisation and every standing invariant
echo   3. THE ACQUISITION - up to 704 EODHD requests, 15-25 minutes
echo   4. post-acquisition verification, read-only
echo   5. full Release suite
echo   ZERO SEC calls. NO dividends. NO scoring, orders or research.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\acquire-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build - nothing was spent
  findstr /C:"error " "%~dp0..\artifacts\verify\acquire-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-acquire.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
