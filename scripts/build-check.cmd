@echo off
setlocal
rem BUILD ONLY. No tests, no network, no database.

cd /d "%~dp0.."

echo.
echo === Release build (warnings are errors) ===
echo.

dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\build-check.log" 2>&1
set EXITCODE=%ERRORLEVEL%

echo Exit code: %EXITCODE%
echo.
findstr /C:"error " "%~dp0..\artifacts\verify\build-check.log"
echo.
echo Full log: artifacts\verify\build-check.log
echo.
pause
endlocal
