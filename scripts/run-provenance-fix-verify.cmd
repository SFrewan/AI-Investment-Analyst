@echo off
setlocal
rem PART A ONLY - provenance assertion correction: build + focused tests + full Release suite.
rem   NO replay. NO recovery. NO write to ai_investment. EVBG is NOT touched.

cd /d "%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-provenance-fix-verify.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
