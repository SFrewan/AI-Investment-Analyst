@echo off
setlocal
rem FIVE-MEMBER RECOVERY - FINAL RECONCILIATION. READ ONLY.
rem   No replay. No provider. No acquisition. No authorisation. No database write.
rem   Gate 6 is NOT modified, NOT re-evaluated and NOT resealed.

cd /d "%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-reconcile.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
