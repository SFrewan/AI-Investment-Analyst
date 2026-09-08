@echo off
REM ---------------------------------------------------------------------------
REM  VLDR pilot - THE ONE LIVE REQUEST.
REM
REM  Runs tools\vldr-pilot in execute mode against the TEST database. The tool
REM  re-runs every precondition first and refuses to reach the network if any of
REM  them fails, so this launcher cannot cause a request that the preflight would
REM  not have permitted.
REM
REM  THE WINDOW: 2023-02-13 to 2023-02-17.
REM
REM  Five consecutive trading sessions, Monday to Friday. The following Monday,
REM  2023-02-20, was Presidents' Day, so the span contains no US market holiday
REM  and no weekend interior.
REM
REM  Why a window inside VLDR's trading life rather than after it: the preflight
REM  measured ZERO observations for VLDR.US in ai_investment_tests - the suite
REM  truncates every mapped table between tests - so there is nothing for a
REM  window to overlap and the append-only duplicate risk does not arise here.
REM  With that constraint genuinely absent, the smallest window that produces a
REM  MEANINGFUL response is one that returns real rows, because that is what
REM  exercises normalisation into observations. A post-series window would
REM  return [] and prove less.
REM
REM  The tool still enforces the rule rather than trusting this comment: it
REM  refuses unless the window begins strictly after the latest stored VLDR
REM  observation, and refuses a span wider than 10 calendar days.
REM
REM  EXACTLY ONE PROVIDER REQUEST. Run this once.
REM ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"

set PILOT_FROM=2023-02-13
set PILOT_TO=2023-02-17

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$scripts = Join-Path (Get-Location) 'scripts';" ^
  "$local = Join-Path $scripts 'verify.local.ps1';" ^
  "if (Test-Path -Path $local) { . $local };" ^
  "$cs = $env:AIINV_TEST_POSTGRES;" ^
  "if ([string]::IsNullOrWhiteSpace($cs)) { Write-Host 'STOPPING. AIINV_TEST_POSTGRES is not set.'; exit 2 };" ^
  "if ($cs -match '(?i)(^|;)\s*database\s*=\s*ai_investment\s*(;|$)') { Write-Host 'STOPPING. AIINV_TEST_POSTGRES names the evidence database.'; exit 4 };" ^
  "$env:AIINV_TEST_POSTGRES = $cs;" ^
  "$root = (Get-Location).Path;" ^
  "dotnet run --project tools\vldr-pilot\VldrPilot.csproj -c Release -- $root execute --from %PILOT_FROM% --to %PILOT_TO%;" ^
  "exit $LASTEXITCODE" > "artifacts\verify\vldr-pilot.log" 2>&1

set EXITCODE=%ERRORLEVEL%
echo.
echo Exit code: %EXITCODE%
echo Log: artifacts\verify\vldr-pilot.log
echo.
pause
endlocal
