@echo off
REM ---------------------------------------------------------------------------
REM  The whole Release test suite, captured to a log.
REM
REM  IT SUPPLIES THE TEST CONNECTION STRING. Without one, PostgresFixture reports
REM  Available = false and every database-backed test SKIPS - 98 of them on this
REM  machine. The run still says "Passed!", which is exactly the hollow green the
REM  fixture's own comments warn about, so this script sets the variable and
REM  refuses to run at all if it cannot.
REM
REM  AIINV_TEST_POSTGRES ONLY, and only when it names a database whose name ends
REM  in _tests - the same interlock PostgresFixture.RequiredDatabaseSuffix
REM  enforces from the other side. The integration tests TRUNCATE every mapped
REM  table between tests, so pointing this at ai_investment would destroy the
REM  evidence base. AIINV_DESIGNTIME_DB is never read here.
REM
REM  It applies no migration and contacts no vendor.
REM ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$scripts = Join-Path (Get-Location) 'scripts';" ^
  "$local = Join-Path $scripts 'verify.local.ps1';" ^
  "if (Test-Path -Path $local) { . $local };" ^
  "$cs = $env:AIINV_TEST_POSTGRES;" ^
  "if ([string]::IsNullOrWhiteSpace($cs)) { Write-Host 'STOPPING. AIINV_TEST_POSTGRES is not set, so 98 database-backed tests would skip and the run would report a green it had not earned.'; exit 2 };" ^
  "if ($cs -notmatch '(?i)(^|;)\s*database\s*=\s*[^;]*_tests\s*(;|$)') { Write-Host 'STOPPING. AIINV_TEST_POSTGRES does not name a _tests database. The integration tests truncate every mapped table.'; exit 4 };" ^
  "if ($cs -match '(?i)(^|;)\s*database\s*=\s*ai_investment\s*(;|$)') { Write-Host 'STOPPING. That is the evidence database.'; exit 4 };" ^
  "$env:Database__ConnectionString = $cs;" ^
  "$env:Providers__Eodhd__Enabled = 'false';" ^
  "$env:Providers__SecEdgar__Enabled = 'false';" ^
  "dotnet test AI-Investment-Analyst.sln -c Release --nologo;" ^
  "exit $LASTEXITCODE" > "artifacts\verify\full-suite.log" 2>&1

set EXITCODE=%ERRORLEVEL%
echo.
echo Exit code: %EXITCODE%
echo Log: artifacts\verify\full-suite.log
echo.
pause
endlocal
