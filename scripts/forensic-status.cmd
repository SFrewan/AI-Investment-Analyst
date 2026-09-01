@echo off
setlocal
rem READ-ONLY FORENSIC STATUS CHECK. Investigation only.
rem   Does NOT start the API. Does NOT enable RunCycles. Does NOT call EODHD.
rem   Does NOT write to the database - the psql session is read-only at the server.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  READ-ONLY FORENSIC STATUS CHECK
echo.
echo  - running processes / port 5143 / PostgreSQL service
echo  - shutdown and boot times
echo  - database state (SELECT only, read-only session)
echo  - live run artifacts and their timestamps
echo  - watch id and first cycle id, searched on disk
echo  - working tree
echo.
echo  Nothing is started. Nothing is changed. No EODHD request.
echo  Transcript: artifacts\verify\forensic-status.txt
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0forensic-status.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
