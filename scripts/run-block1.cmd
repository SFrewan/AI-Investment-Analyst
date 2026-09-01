@echo off
setlocal
rem BLOCK 1 - CORRECTNESS AND PRODUCTION WIRING
rem   sweep -> Release build -> focused tests -> full suite
rem   No EODHD request. No cycle. No cooldown wait. No rule changed.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  BLOCK 1 - CORRECTNESS AND PRODUCTION WIRING
echo.
echo   1. read-only sweep: unregistered deps, weak endpoint tests
echo   2. Release build (warnings are errors)
echo   3. focused tests: composition + portfolio
echo   4. full Release suite
echo.
echo  Nothing here calls a provider or starts a cycle.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-block1.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
