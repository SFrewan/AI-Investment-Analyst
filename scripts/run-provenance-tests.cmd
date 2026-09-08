@echo off
setlocal
rem PER-RUN REQUEST PROVENANCE - build + focused provenance tests + architecture tests.
rem   No migration. No schema change. No provider call.

cd /d "%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-provenance-tests.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
