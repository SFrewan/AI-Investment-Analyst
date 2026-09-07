@echo off
setlocal
rem READ-ONLY. What price history is actually held. SELECT only, no network request.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0inspect-price-depth.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
