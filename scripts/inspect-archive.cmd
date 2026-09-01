@echo off
setlocal
rem BLOCK 2B - READ-ONLY ARCHIVE INSPECTION. Reads stored payloads. No network request.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  BLOCK 2B - WHAT THE VENDOR ACTUALLY SENT
echo   Reads the archived raw payloads to settle two questions
echo   without making another call.
echo   No EODHD request. No token printed. Nothing is changed.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0inspect-archive.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
