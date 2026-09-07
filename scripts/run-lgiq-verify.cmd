@echo off
setlocal
rem POST-RUN VERIFICATION FOR SEC BATCH 2 (LGIQ)
rem   NO NETWORK CALL OF ANY KIND. Nothing is dispatched, nothing is consumed,
rem   no batch is authorised. Both connectors stay off for the whole run.
rem   1. Release build, warnings are errors
rem   2. A read-only census of the payload batch 2 archived
rem   3. The focused SEC facts, then the full Release suite

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  POST-RUN VERIFICATION - SEC BATCH 2 - LGIQ.US
echo   ZERO SEC calls. ZERO EODHD calls. ZERO network requests.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\lgiq-verify-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\lgiq-verify-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-lgiq-verify.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
