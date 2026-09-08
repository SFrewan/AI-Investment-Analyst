@echo off
setlocal
rem ROW-LEVEL PRICE QUARANTINE - BUILD AND TEST
rem   NO NETWORK CALL OF ANY KIND. No acquisition, no authorisation change,
rem   no archived payload re-fetched. Both connectors stay off.
rem   1. Release build, warnings are errors
rem   2. Focused normalisation / pipeline / coverage / splits / quarantine facts
rem   3. Full Release suite

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  ROW-LEVEL PRICE QUARANTINE - build and test
echo   ZERO network calls. ZERO acquisitions. Gate 6 untouched.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\rowlevel-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\rowlevel-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-rowlevel.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
