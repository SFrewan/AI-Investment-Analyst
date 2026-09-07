@echo off
setlocal
rem READ-ONLY. Inspects the newest archived SEC payload and the quarantine table.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0inspect-sec-payload.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
