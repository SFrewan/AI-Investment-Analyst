@echo off
setlocal
rem LOCAL ARCHIVED-PAYLOAD RECOVERY - GPP.US ONLY. THIS ONE WRITES.
rem   ONE replay, ORIGINAL run identity (4c1bc92f-ade8-4535-8322-053e1dcafbbb), archived bytes.
rem   NO EODHD call. NO SEC call. NO acquisition authorisation. NO new ingestion run.
rem   NO CCF replay. NO EVBG replay. NO VLDR / WIRE replay.

cd /d "%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-recover-gpp.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
