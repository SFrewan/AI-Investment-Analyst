@echo off
setlocal
rem FINAL PRE-RECOVERY PREFLIGHT - net8.0 tool. READ ONLY.
rem   NO recovery. NO replay of any target. NO normalisation. NO write of any kind.
rem   Windows PowerShell is only a launcher here; it compiles nothing.

cd /d "%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-replay-preflight-net8.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
