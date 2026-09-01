@echo off
setlocal
rem READ-ONLY NETWORK DIAGNOSIS. Makes no EODHD API request and sends no token.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  NETWORK DIAGNOSIS - read only
echo   Works out whether eodhd.com failing to resolve is the network,
echo   this machine's DNS, or that one name.
echo   No API request. No token. Nothing is changed.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0diagnose-network.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
