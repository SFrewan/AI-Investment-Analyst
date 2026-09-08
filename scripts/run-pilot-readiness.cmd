@echo off
REM ---------------------------------------------------------------------------
REM  Double-clickable launcher for scripts\run-pilot-readiness.ps1.
REM
REM  Runs the scaffolding guard's tests, resolves the scaffolding target on this
REM  machine WITHOUT scaffolding anything, and runs the focused provenance and
REM  composition tests.
REM
REM  It applies no migration, contacts no vendor, creates no authorization and
REM  writes to no database. See the header of the .ps1 for how each of those is
REM  guaranteed rather than intended.
REM ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0.."
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\run-pilot-readiness.ps1"
set EXITCODE=%ERRORLEVEL%
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
