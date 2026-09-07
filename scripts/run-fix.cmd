@echo off
setlocal
rem ATTEMPT-IDENTITY FIX: BUILD, TEST, AUDIT. NO NETWORK.
rem   ZERO EODHD calls. ZERO SEC calls. No price, split, dividend or benchmark acquisition.
rem   NXST.US and SIRI.US are NOT executed. No batch runs. No acquisition switch is set.
rem   No authorisation is created or amended. No manifest, identity or parser change.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  ATTEMPT-IDENTITY FIX - CODE, TESTS AND AUDIT ONLY
echo   1. Release build, warnings are errors
echo   2. attempt identity, the seam, the data plane rule
echo   3. full Release suite
echo   4. read-only state audit
echo   NOTHING IS ACQUIRED. NXST.US AND SIRI.US DO NOT RUN.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\fix-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\fix-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-fix.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
