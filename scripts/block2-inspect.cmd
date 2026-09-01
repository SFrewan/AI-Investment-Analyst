@echo off
setlocal
rem BLOCK 2 - read-only inspection of the evidence base. No writes, no network.
cd /d "%~dp0.."
echo.
echo ===============================================================
echo  BLOCK 2 - EVIDENCE BASE INSPECTION (read-only)
echo ===============================================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0block2-inspect.ps1"
echo.
echo Exit code: %ERRORLEVEL%
echo.
pause
endlocal
