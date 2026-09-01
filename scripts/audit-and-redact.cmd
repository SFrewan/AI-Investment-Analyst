@echo off
setlocal
rem 1. Redact provider credentials from the verification logs (evidence preserved).
rem 2. Read-only repository inventory for the audit.
rem Neither step starts the API, touches the database, or makes a network request.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  STEP 1 of 2 - credential redaction (no evidence deleted)
echo ===============================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0redact-api-token.ps1"
echo Exit code: %ERRORLEVEL%

echo.
echo ===============================================================
echo  STEP 2 of 2 - read-only repository inventory
echo ===============================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0audit-inventory.ps1"
echo Exit code: %ERRORLEVEL%

echo.
echo Done. Reports are in artifacts\audit
echo.
pause
endlocal
