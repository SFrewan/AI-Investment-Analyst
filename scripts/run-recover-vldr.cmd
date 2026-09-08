@echo off
setlocal
rem LOCAL ARCHIVED-PAYLOAD RECOVERY - VLDR.US ONLY. THIS ONE WRITES.
rem   ONE replay, ORIGINAL run identity (e4097a2d-1db7-4c85-9388-6b45cad106c4), archived bytes.
rem   NO EODHD call. NO SEC call. NO acquisition authorisation. NO new ingestion run.
rem   NO CCF / EVBG / GPP replay. NO WIRE replay.

cd /d "%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-recover-vldr.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
