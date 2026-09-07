@echo off
setlocal
rem PRE-ACQUISITION REMEDIATION.
rem   At most 5 FREE SEC requests for the historical-ticker probe. ZERO EODHD calls.
rem   NO prices. NO splits. NO dividends. NO corporate actions. NO scoring. NO orders.
rem   The sealed universe is not modified.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  PRE-ACQUISITION REMEDIATION - us-pit-sample400 @ f78cfe45b962
echo   1. Release build, warnings are errors
echo   2. historical ticker probe, max 5 FREE SEC calls
echo   3. session-close re-stamp of stored prices, zero calls
echo   4. session resolver, coverage rule, planning, identity
echo   5. full Release suite
echo   ZERO EODHD calls. NO market-data acquisition.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\remediation2-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\remediation2-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-remediation2.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
