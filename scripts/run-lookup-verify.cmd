@echo off
setlocal
rem ARCHIVED-RUN LOOKUP - VERIFICATION AGAINST THE REAL DATABASE. READ ONLY.
rem   Every statement is a SELECT, inside a READ ONLY transaction, rolled back.
rem   NO recovery. NO replay. NO normalisation. NO write of any kind.
rem   Needs PowerShell 7: Npgsql is a .NET 8 assembly and Windows PowerShell 5.1 cannot load it.

cd /d "%~dp0.."

where pwsh.exe >nul 2>&1
if errorlevel 1 (
  echo   FAIL  PowerShell 7 ^(pwsh.exe^) was not found on PATH.
  echo         Npgsql is a .NET 8 assembly; Windows PowerShell 5.1 runs on .NET Framework
  echo         and cannot load it. Nothing was read and nothing was written.
  set EXITCODE=5
  goto :done
)

pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-lookup-verify.ps1"
set EXITCODE=%ERRORLEVEL%

:done
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
