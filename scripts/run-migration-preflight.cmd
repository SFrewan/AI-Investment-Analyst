@echo off
setlocal
rem PRODUCTION PROVENANCE MIGRATION - PREFLIGHT. READ ONLY.
rem   Describes ai_investment. Applies nothing. Changes no script and no guard.

cd /d "%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-migration-preflight.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
