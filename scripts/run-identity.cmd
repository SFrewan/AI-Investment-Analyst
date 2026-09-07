@echo off
setlocal
rem SEC IDENTITY COMPLETION PASS.
rem   Free EDGAR submissions requests for the sealed 400, hard ceiling 367.
rem   NO EODHD calls. NO price acquisition. NO corporate actions. NO scoring.
rem   NO strategy declarations. NO orders. NO threshold changed.
rem   The sealed 400-member universe is NOT modified: membership is read from the manifest,
rem   checked against its fingerprint, and the identity table is written beside it.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  SEC IDENTITY COMPLETION - us-pit-sample400 @ f78cfe45b962
echo   1. Release build, warnings are errors
echo   2. up to 367 free EDGAR submissions requests
echo   3. reconcile SEC identity against provisional EODHD tickers
echo   4. re-run the gates, acquisition-dependent ones deferred
echo   5. full Release suite
echo   NO EODHD calls. NO prices. NO scoring. NO declarations.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\identity-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\identity-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-identity.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Identity table: artifacts\universe\identity-sample400.json
echo Report:         artifacts\verify\universe-identity.md
echo.
pause
endlocal
