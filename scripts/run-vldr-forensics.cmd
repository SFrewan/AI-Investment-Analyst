@echo off
REM ---------------------------------------------------------------------------
REM  Read-only forensics on the VLDR pilot.
REM
REM  SELECT only. It contacts no provider, writes no row and repairs nothing.
REM  Safe to run more than once.
REM ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$scripts = Join-Path (Get-Location) 'scripts';" ^
  "$local = Join-Path $scripts 'verify.local.ps1';" ^
  "if (Test-Path -Path $local) { . $local };" ^
  "$cs = $env:AIINV_TEST_POSTGRES;" ^
  "if ([string]::IsNullOrWhiteSpace($cs)) { Write-Host 'STOPPING. AIINV_TEST_POSTGRES is not set.'; exit 2 };" ^
  "$env:AIINV_TEST_POSTGRES = $cs;" ^
  "$root = (Get-Location).Path;" ^
  "dotnet run --project tools\vldr-forensics\VldrForensics.csproj -c Release -- $root;" ^
  "exit $LASTEXITCODE" > "artifacts\verify\vldr-forensics.log" 2>&1

set EXITCODE=%ERRORLEVEL%
echo.
echo Exit code: %EXITCODE%
echo Log: artifacts\verify\vldr-forensics.log
echo.
pause
endlocal
