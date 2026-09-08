@echo off
REM ---------------------------------------------------------------------------
REM  VLDR pilot - PREFLIGHT ONLY.
REM
REM  Reads the TEST database and the connector configuration and reports what it
REM  finds. It contacts no provider, writes no row and registers nothing: the
REM  program stops before any of that when the mode is 'preflight'.
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
  "if ($cs -match '(?i)(^|;)\s*database\s*=\s*ai_investment\s*(;|$)') { Write-Host 'STOPPING. AIINV_TEST_POSTGRES names the evidence database.'; exit 4 };" ^
  "$env:AIINV_TEST_POSTGRES = $cs;" ^
  "$root = (Get-Location).Path;" ^
  "dotnet run --project tools\vldr-pilot\VldrPilot.csproj -c Release -- $root preflight;" ^
  "exit $LASTEXITCODE" > "artifacts\verify\vldr-preflight.log" 2>&1

set EXITCODE=%ERRORLEVEL%
echo.
type "artifacts\verify\vldr-preflight.log"
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
