@echo off
setlocal
rem ROW-LEVEL CENSUS - READ-ONLY MEASUREMENT OF THE ELEVEN ARCHIVED PAYLOADS
rem   NO network call. NO acquisition. NO authorisation touched.
rem   NOTHING is recovered or stored. Gate 6 is NOT modified.

cd /d "%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-rowlevel-census.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
