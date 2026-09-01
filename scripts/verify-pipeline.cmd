@echo off
setlocal
rem PIPELINE VERIFICATION, SEPARATED FROM COOLDOWN VERIFICATION.
rem   Build -> focused tests -> full suite -> reachability -> ONE live cycle -> read-only DB check.
rem   The production 4-hour cooldown is NOT waited for and NOT changed.
rem   Leave this window open. Nothing is left running when it finishes.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  PIPELINE VERIFICATION - no four-hour wait, cooldown untouched
echo.
echo   1. Release build
echo   2. focused tests (incl. the two cooldown suppression proofs)
echo   3. full suite - the live test reports as SKIPPED here
echo   4. provider reachability pre-flight (no token, no data)
echo   5. ONE live cycle through the isolated verification path
echo   6. read-only database corroboration
echo.
echo  Phases 4-6 run only if 1-3 pass.
echo  Phase 5 makes ONE real EODHD request. The trigger key carries the
echo  current UTC hour, so re-running inside the hour resumes that cycle
echo  instead of buying another.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-pipeline.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
