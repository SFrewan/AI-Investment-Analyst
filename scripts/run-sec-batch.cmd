@echo off
setlocal
rem ONE BATCH OF THE SEC EDGAR FILING ACQUISITION.
rem   Usage: run-sec-batch.cmd 1
rem
rem   THIS IS THE EXECUTION DOOR. IT IS WIRED AND IT IS SHUT.
rem   Running it today dispatches NOTHING: every batch is marked unauthorised and
rem   the runner refuses on that at its first gate, spending no authorisation unit.
rem
rem   Once a batch IS approved: sec-edgar ONLY, ONE CIK, ONE request, NO window on
rem   the provider request. The authorisation scope window 2021-09-01..2026-08-31 is
rem   used for Covers() and is never sent to the provider. Ceiling of 6 enforced,
rem   not amended. EODHD stays off throughout.
rem
rem   Refuses before anything if AIINV_SEC_CONTACT is not set. The value is never printed.
rem   The next batch does NOT run: each requires its own approval.

cd /d "%~dp0.."

if "%~1"=="" (
  echo.
  echo   No batch number given. There is no default batch.
  echo   Usage: scripts\run-sec-batch.cmd 1
  echo.
  exit /b 2
)

echo.
echo ===============================================================
echo  SEC BATCH %~1 - us-pit-sample400 @ f78cfe45b962
echo   1. Release build, warnings are errors
echo   2. THE BATCH - at most ONE EDGAR request for ONE CIK
echo   3. runner invariants, connectors off
echo   4. full Release suite
echo   ZERO EODHD calls. NO prices. NO splits. NO scoring or orders.
echo   REFUSES AND SPENDS NOTHING WHILE NO BATCH IS APPROVED.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\sec-batch-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build - nothing was spent
  findstr /C:"error " "%~dp0..\artifacts\verify\sec-batch-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-sec-batch.ps1" -Batch %~1
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
