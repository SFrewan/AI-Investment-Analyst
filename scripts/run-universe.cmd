@echo off
setlocal
rem THE FOUR-HUNDRED-COMPANY POINT-IN-TIME UNIVERSE MANIFEST.
rem   Authorised: ~821 free SEC calls + exactly 2 billable EODHD calls.
rem   NO price/splits acquisition. NO price history. NO declarations. NO scoring. NO trading.
rem   Every stage is resumable: a company already held is skipped before a request is built.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  POINT-IN-TIME UNIVERSE - 400 COMPANIES
echo   0. Release build, warnings are errors
echo   1. frames    7 free EDGAR cross-section requests
echo   2. profiles  400 free EDGAR submissions requests
echo   3. facts     400 free EDGAR companyfacts requests
echo   4. manifest  exactly 2 billable EODHD calls, seal, twelve gates
echo   5. full Release suite
echo   NO acquisition. NO price history. NO declarations. NO scoring.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\universe-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\universe-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-universe.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Manifest: declarations\universe-400-2021-2026.json
echo Roster:   artifacts\universe\roster.json
echo Reports:  artifacts\verify\universe-frames.md
echo           artifacts\verify\universe-profiles.md
echo           artifacts\verify\universe-facts.md
echo           artifacts\verify\universe-manifest-400.md
echo.
pause
endlocal
