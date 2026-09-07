@echo off
setlocal
rem WIDENED SURVIVORSHIP PROBE.
rem   At most 5 FREE SEC requests for the historical-ticker probe. ZERO EODHD calls.
rem   NO prices. NO splits. NO dividends. NO corporate actions. NO scoring. NO orders.
rem   The sealed universe is not modified.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  WIDENED SURVIVORSHIP PROBE - us-pit-sample400 @ f78cfe45b962
echo   1. Release build, warnings are errors
echo   2. widened survivorship probe, sample frozen first, max 10 FREE SEC calls
echo   3. selection rule tests
echo   4. full Release suite
echo   5. full Release suite
echo   ZERO EODHD calls. NO market-data acquisition.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\probe-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\probe-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-probe.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
