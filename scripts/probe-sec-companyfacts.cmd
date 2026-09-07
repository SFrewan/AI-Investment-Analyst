@echo off
setlocal
rem READ-ONLY. Probes the companyfacts endpoint for the two companies that fail.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0probe-sec-companyfacts.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
