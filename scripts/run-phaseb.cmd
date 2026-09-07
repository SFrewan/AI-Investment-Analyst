@echo off
setlocal
rem PHASE B - SEC EDGAR RUNNER. IMPLEMENTATION AND TESTING ONLY. ZERO DISPATCH.
rem   ZERO SEC calls. ZERO EODHD calls. ZERO network. The SEC connector stays DISABLED.
rem   Nothing calls the runner except tests driving it with fakes; there is no
rem   environment-variable door wired to the real container in this phase.
rem   NO AUTHORIZATION UNIT IS CONSUMED: every authorisation exercised is synthetic,
rem   written to the temp directory and deleted; the installed one is read-only.
rem   No batch is authorised. The rate limiter is NOT moved: it remains the
rem   side-effecting gate inside the dispatch path.
rem   Gate 6, the sealed manifest, the universe, the EODHD declarations and data,
rem   the ledger and the database are NOT modified.
rem   One report is written: artifacts\verify\sec-edgar-runner-phase-b-report.md

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  PHASE B - SEC EDGAR RUNNER, TEST ONLY, ZERO DISPATCH
echo   1. Release build, warnings are errors
echo   2. focused SEC runner tests
echo   3. Phase A preflight + normaliser tests
echo   4. full Release suite
echo   NOTHING IS DISPATCHED. NOTHING IS AUTHORISED. NOTHING IS CONSUMED.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\phaseb-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\phaseb-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-phaseb.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
