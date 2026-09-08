@echo off
setlocal
rem FINAL PRE-RECOVERY PREFLIGHT - the real C# path against the real database. READ ONLY.
rem   NO recovery. NO replay of any target. NO normalisation. NO write of any kind.
rem   Needs PowerShell 7: the assemblies are .NET 8.

cd /d "%~dp0.."

where pwsh.exe >nul 2>&1
if errorlevel 1 (
  echo   FAIL  PowerShell 7 ^(pwsh.exe^) was not found on PATH.
  set EXITCODE=5
  goto :done
)

pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-replay-preflight.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
