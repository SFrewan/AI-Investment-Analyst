@echo off
setlocal
rem THE SEC CANARY - BATCH 1 - QUMU.US - CIK 0000892482
rem   THIS STEP REACHES THE U.S. SECURITIES AND EXCHANGE COMMISSION.
rem   ONE company. ONE request. ONE authorisation unit of six.
rem   submissions/CIK0000892482.json, category RegulatoryFilings, NO window on the
rem   provider request. The authorisation scope window 2021-09-01..2026-08-31 is
rem   used for Covers() only. Ceiling of 6 enforced, not amended.
rem   EODHD stays off. Batches 2-6 do NOT run.
rem   Refuses before anything if AIINV_SEC_CONTACT is not set; never prints it.
rem   No full suite runs here: the batch flag is returned to false first.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  SEC CANARY - BATCH 1 - QUMU.US - CIK 0000892482
echo   1. Release build, warnings are errors
echo   2. THE CANARY - exactly ONE EDGAR request
echo   ZERO EODHD calls. NO prices. NO splits. NO other CIK.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\sec-canary-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build - nothing was spent
  findstr /C:"error " "%~dp0..\artifacts\verify\sec-canary-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-canary.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
