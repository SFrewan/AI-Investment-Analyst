@echo off
setlocal
rem BLOCK 2 - build and test. No provider call, no cycle, no cooldown wait.
cd /d "%~dp0.."
echo.
echo ===============================================================
echo  BLOCK 2 - EVIDENCE BASE (build + tests only)
echo ===============================================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-block2.ps1" %*
echo.
echo Exit code: %ERRORLEVEL%
echo.
pause
endlocal
