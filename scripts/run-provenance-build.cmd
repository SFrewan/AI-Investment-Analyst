@echo off
setlocal
rem PER-RUN REQUEST PROVENANCE - BUILD ONLY. No tests, no migration, no database, no provider.

cd /d "%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-provenance-build.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
