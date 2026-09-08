@echo off
setlocal
rem LOCAL ARCHIVED-PAYLOAD REPLAY - BUILD AND TEST
rem   NO NETWORK CALL OF ANY KIND. No acquisition, no authorisation change,
rem   no archived payload re-fetched, NO RECOVERY PERFORMED.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  LOCAL ARCHIVED-PAYLOAD REPLAY - build and test
echo   ZERO network calls. ZERO acquisitions. NO RECOVERY PERFORMED.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\replay-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\replay-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-replay.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
