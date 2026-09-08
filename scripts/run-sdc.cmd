@echo off
setlocal
rem SEC BATCH 5 - SDC.US - CIK 0001775625
rem   THIS STEP REACHES THE U.S. SECURITIES AND EXCHANGE COMMISSION.
rem   ONE company. ONE request. ONE authorisation unit, the fifth of six.
rem   submissions/CIK0001775625.json, category RegulatoryFilings, NO window on the
rem   provider request. The authorisation scope window 2021-09-01..2026-08-31 is
rem   used for Covers() only. Ceiling of 6 enforced, not amended.
rem   Seventeen shared forms; the secondary scope belongs to LGIQ alone.
rem   EODHD stays off. Batches 1-4 and 6 do NOT run.
rem   Ten pre-dispatch assertions are checked from disk first; any failure stops
rem   before anything is spent. Refuses if AIINV_SEC_CONTACT is absent or malformed;
rem   never prints it. No full suite runs here.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  SEC BATCH 5 - SDC.US - CIK 0001775625
echo   1. Release build, warnings are errors
echo   2. Ten pre-dispatch assertions, read from disk
echo   3. THE RUN - exactly ONE EDGAR request
echo   ZERO EODHD calls. NO prices. NO splits. NO other CIK.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\sec-sdc-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build - nothing was spent
  findstr /C:"error " "%~dp0..\artifacts\verify\sec-sdc-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-sdc.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
