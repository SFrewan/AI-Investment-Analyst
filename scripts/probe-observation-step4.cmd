@echo off
setlocal
rem OBSERVATION WINDOW - STEP 4 PRE-ACTIVATION VERIFICATION. READ ONLY.
rem Changes nothing: no activation, no watch, no EODHD request, no database write.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  OBSERVATION WINDOW - STEP 4 PRE-ACTIVATION VERIFICATION
echo  READ ONLY - nothing is activated, written or requested.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0probe-observation-step4.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
