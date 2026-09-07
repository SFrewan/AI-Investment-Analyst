@echo off
setlocal
rem READ-ONLY. Reads why the SEC ingestion is being refused. SELECT only, no network request.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0inspect-sec-refusal.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
