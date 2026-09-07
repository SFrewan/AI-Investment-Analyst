@echo off
setlocal
rem PRE-ACQUISITION DRY RUN.
rem   ZERO provider calls. No connector is switched on: neither EODHD nor SEC is reachable.
rem   NO prices. NO splits. NO dividends. NO corporate actions. NO scoring. NO orders.
rem   The sealed universe is not modified and is asserted byte-identical either side.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  PRE-ACQUISITION DRY RUN - us-pit-sample400 @ f78cfe45b962
echo   1. Release build, warnings are errors
echo   2. the sealed coverage rule and every standing invariant
echo   3. the acquisition dry run - what WOULD be requested, nothing sent
echo   4. full Release suite
echo   ZERO EODHD calls. ZERO SEC calls. NO market-data acquisition.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\dryrun-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\dryrun-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-dryrun.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
