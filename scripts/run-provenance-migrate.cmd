@echo off
setlocal
rem PER-RUN REQUEST PROVENANCE - apply the migration to the DEVELOPMENT database, then verify.
rem   Refuses to run if the configured connection names ai_investment.
rem   Additive only. No provider call. No acquisition. No replay.

cd /d "%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-provenance-migrate.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
