@echo off
setlocal
rem ONE BATCH OF THE SPLIT ACQUISITION.
rem   Usage: run-splits.cmd 2
rem   SPENDS AT MOST 70 PROVIDER REQUESTS, for the named batch only.
rem   eodhd-splits ONLY. NO prices. NO dividends. NO SPY. NO SEC call.
rem   Window 2021-09-01..2026-08-31. The 352 ceiling is enforced, not amended.
rem   The sealed universe and the identity table are neither modified nor resealed.
rem   The next batch does NOT run: it is marked unauthorised in the runner.

cd /d "%~dp0.."

if "%~1"=="" (
  echo.
  echo   No batch number given. There is no default batch.
  echo   Usage: scripts\run-splits.cmd 1
  echo.
  exit /b 2
)

echo.
echo ===============================================================
echo  SPLIT BATCH %~1 - us-pit-sample400 @ f78cfe45b962
echo   1. Release build, warnings are errors
echo   2. the authorisation and every standing invariant
echo   3. THE BATCH - at most 70 EODHD split requests, 2-4 minutes
echo   4. post-acquisition verification, read-only
echo   5. quarantine survey, read-only, nothing reprocessed
echo   6. full Release suite
echo   ZERO SEC calls. NO prices. NO dividends. NO scoring or orders.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\splits-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build - nothing was spent
  findstr /C:"error " "%~dp0..\artifacts\verify\splits-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-splits.ps1" -Batch %~1
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
