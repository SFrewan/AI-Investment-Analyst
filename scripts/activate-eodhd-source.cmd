@echo off
setlocal
rem OBSERVATION WINDOW - STEP 4: ACTIVATE eodhd-eod.
rem You will be prompted for the operator key. Input is hidden and is never written to a file.
rem No watch is created. RunCycles is not enabled. No EODHD request is made.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  OBSERVATION WINDOW - STEP 4: ACTIVATE eodhd-eod
echo.
echo  This sends ONE authenticated request:
echo    POST http://localhost:5143/api/sources/eodhd-eod/activation
echo.
echo  It does NOT create a watch, does NOT enable RunCycles, and
echo  does NOT call the EODHD API.
echo.
echo  Close this window now if you do not want to proceed.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0activate-eodhd-source.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
