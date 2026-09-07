@echo off
setlocal
rem BUILD ONLY. Nothing is executed, nothing is acquired, no provider is contacted.
rem   Release build with warnings as errors, so a compile error is found before any
rem   stage that could spend a request is started.

cd /d "%~dp0.."

echo.
echo === Release build (warnings are errors). NOTHING IS ACQUIRED.
echo.

dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\batch-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%

if "%EXITCODE%"=="0" (
  echo   PASS  Release build
) else (
  echo   FAIL  Release build
  echo.
  findstr /C:"error " "%~dp0..\artifacts\verify\batch-build.log"
)

echo.
echo Exit code: %EXITCODE%
echo Full log: artifacts\verify\batch-build.log
echo.
pause
endlocal
