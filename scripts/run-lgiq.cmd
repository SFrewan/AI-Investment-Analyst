@echo off
setlocal
rem SEC BATCH 2 - LGIQ.US - CIK 0001335112
rem   THIS STEP REACHES THE U.S. SECURITIES AND EXCHANGE COMMISSION.
rem   ONE company. ONE request. ONE authorisation unit, the second of six.
rem   submissions/CIK0001335112.json, category RegulatoryFilings, NO window on the
rem   provider request. The authorisation scope window 2021-09-01..2026-08-31 is
rem   used for Covers() only. Ceiling of 6 enforced, not amended.
rem   EODHD stays off. Batch 1 does NOT run again. Batches 3-6 do NOT run.
rem   Refuses before anything if AIINV_SEC_CONTACT is absent or malformed; never prints it.
rem   No full suite runs here: the batch flag is returned to false afterwards.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  SEC BATCH 2 - LGIQ.US - CIK 0001335112
echo   1. Release build, warnings are errors
echo   2. THE RUN - exactly ONE EDGAR request
echo   ZERO EODHD calls. NO prices. NO splits. NO other CIK.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\sec-lgiq-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build - nothing was spent
  findstr /C:"error " "%~dp0..\artifacts\verify\sec-lgiq-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-lgiq.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
