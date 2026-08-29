@echo off
setlocal
rem BLOCKER 1: add AnswerEscalations + ViewPortfolio to the existing operator account,
rem then check the raw archive is writable.
rem Does NOT touch the operator key/digest, Id, DisplayName or any other secret.
rem Does NOT enable RunCycles, create a watch, start a cycle or call EODHD.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  BLOCKER 1 - OPERATOR PRIVILEGES + ARCHIVE CHECK
echo.
echo  Adds exactly two User Secrets keys:
echo    ...Privileges:n   = AnswerEscalations
echo    ...Privileges:n+1 = ViewPortfolio
echo.
echo  The key digest, Id, DisplayName and AdministerWatches are
echo  left untouched. You will be shown the change and asked to
echo  type UPDATE before anything is written.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0grant-operator-privileges.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
echo If the update succeeded, RESTART THE API so it re-reads Operators.
echo.
pause
endlocal
