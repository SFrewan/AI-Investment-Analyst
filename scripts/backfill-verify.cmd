@echo off
setlocal
rem BLOCK 2B - READ-ONLY VERIFICATION. SELECT only. No network request. No API call.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  BLOCK 2B - READ-ONLY VERIFICATION
echo   What the backfill actually landed: history span, split
echo   observations, and why AAPL prices were refused.
echo   SELECT only. Nothing is changed. No EODHD request.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0backfill-verify.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
