@echo off
setlocal
rem READ-ONLY verification of the audit's load-bearing claims against the working tree.
rem Reads only. No API, no database, no network.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  AUDIT CLAIM VERIFICATION (read-only)
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0audit-verify-claims.ps1"

echo.
echo Exit code: %ERRORLEVEL%
echo.
pause
endlocal
