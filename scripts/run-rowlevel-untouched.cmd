@echo off
setlocal
rem ROW-LEVEL QUARANTINE - UNTOUCHED PROOF. READ-ONLY.
rem   NO network call. NO acquisition. NOTHING recovered or stored.

cd /d "%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-rowlevel-untouched.ps1" > "%~dp0..\artifacts\verify\rowlevel-untouched.log" 2>&1
set EXITCODE=%ERRORLEVEL%

type "%~dp0..\artifacts\verify\rowlevel-untouched.log"

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
