@echo off
setlocal
rem THE THREE GATES - deterministic tests only. No EODHD request. No billing.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  GATE TESTS - deterministic
echo   Ledger/idempotency consistency, and the corporate-actions
echo   path proved end to end against known splits.
echo   No EODHD request. No cycle. No cooldown wait.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
