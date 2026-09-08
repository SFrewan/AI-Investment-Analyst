@echo off
setlocal
rem LOCAL ARCHIVED-PAYLOAD RECOVERY - EVBG.US ONLY. THIS ONE WRITES.
rem   ONE replay, ORIGINAL run identity (e2f77177-4827-43bf-8951-2f56a28c0f2e), archived bytes.
rem   NO EODHD call. NO SEC call. NO acquisition authorisation. NO new ingestion run.
rem   NO CCF replay. NO GPP / VLDR / WIRE replay.

cd /d "%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-recover-evbg.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
