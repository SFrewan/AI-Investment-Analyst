@echo off
setlocal
rem READ-ONLY. What fundamental evidence is now available. SELECT only, no network request.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0inspect-sec-evidence.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
