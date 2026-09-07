@echo off
setlocal
rem READ-ONLY. Final SEC backfill inspection. SELECT only, no network request.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0inspect-sec-final.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
