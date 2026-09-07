@echo off
setlocal
rem PREPARE AND PROVE THE SEC EDGAR SIX-MEMBER PARTITION - READ-ONLY. NO ACQUISITION.
rem   ZERO SEC calls. ZERO EODHD calls. ZERO network. Local files and the loader only.
rem   No acquisition switch is set. NO SPLIT BATCH RUNS and NONE IS AUTHORISED.
rem   THE PARTITION IS NOT A RUNNER: it is a table of facts in the test project, every
rem   batch unauthorised, and the focused tests read its own source to prove it holds
rem   no provider, opens no scope and charges no authorisation.
rem   The installed declaration, its digest, its consumption counter, Gate 6, the sealed
rem   manifest, the universe, the ledger and the database are NOT modified.
rem   One report is written: artifacts\verify\sec-edgar-six-member-partition-report.md

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  SEC EDGAR SIX-MEMBER PARTITION - READ-ONLY, NO ACQUISITION
echo   1. Release build, warnings are errors
echo   2. focused partition tests
echo   3. full Release suite
echo   NOTHING IS ACQUIRED. NOTHING IS AUTHORISED. NOTHING IS CHARGED.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\partition-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\partition-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-partition.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
