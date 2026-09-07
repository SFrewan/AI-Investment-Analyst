@echo off
setlocal
rem PHASE A - FILINGS NORMALIZER + PRE-DISPATCH CONSUMPTION SAFETY. NO ACQUISITION.
rem   ZERO SEC calls. ZERO EODHD calls. ZERO network. The SEC connector stays DISABLED,
rem   and the only connector these tests use is a stub whose FetchAsync throws.
rem   NO AUTHORIZATION UNIT IS CONSUMED: the consumption tests use a synthetic
rem   declaration in the temp directory, and the installed one is loaded read-only.
rem   No batch is authorised. Gate 6, the sealed manifest, the universe, the EODHD
rem   declarations and data, the ledger and the database are NOT modified.
rem   One report is written: artifacts\verify\sec-edgar-phase-a-report.md

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  PHASE A - FILINGS NORMALIZER + CONSUMPTION SAFETY
echo   1. Release build, warnings are errors
echo   2. focused filings-normaliser tests
echo   3. focused pre-dispatch consumption tests
echo   4. full Release suite
echo   NOTHING IS ACQUIRED. NOTHING IS AUTHORISED. NOTHING IS CONSUMED.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\phasea-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\phasea-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-phasea.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
