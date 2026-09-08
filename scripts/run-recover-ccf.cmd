@echo off
setlocal
rem LOCAL ARCHIVED-PAYLOAD RECOVERY - CCF.US ONLY. THIS ONE WRITES.
rem   ONE replay, ORIGINAL run identity, archived bytes already on disk.
rem   NO EODHD call. NO SEC call. NO acquisition authorisation. NO new ingestion run.

cd /d "%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-recover-ccf.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
