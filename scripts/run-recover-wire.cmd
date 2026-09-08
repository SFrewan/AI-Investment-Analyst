@echo off
setlocal
rem LOCAL ARCHIVED-PAYLOAD RECOVERY - WIRE.US ONLY. THE LAST OF THE FIVE. THIS ONE WRITES.
rem   ONE replay, ORIGINAL run identity (bc0eb767-473d-481d-ad3d-d9d9bc491c50), archived bytes.
rem   NO EODHD call. NO SEC call. NO acquisition authorisation. NO new ingestion run.
rem   NO CCF / EVBG / GPP / VLDR replay. NOTHING FURTHER IS STARTED AFTER THIS.

cd /d "%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-recover-wire.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
