@echo off
setlocal
rem FAILURE DIAGNOSIS. READ-ONLY.
rem   ZERO provider calls. Both connectors are switched OFF explicitly, not merely un-overridden.
rem   NO prices. NO splits. NO dividends. NO corporate actions. NO scoring. NO orders.
rem   Nothing is written to the store; the sealed universe is neither modified nor resealed.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  FAILURE DIAGNOSIS - us-pit-sample400 @ f78cfe45b962
echo   1. Release build, warnings are errors
echo   2. failure diagnosis, read-only
echo   3. every standing invariant
echo   4. full Release suite
echo   ZERO EODHD calls. ZERO SEC calls. NO market-data acquisition.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\diagnose-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\diagnose-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-diagnose.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
