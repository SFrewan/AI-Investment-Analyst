@echo off
REM ---------------------------------------------------------------------------
REM  Build the two affected test projects, then run the three focused suites.
REM
REM  It supplies AIINV_TEST_POSTGRES, because ProviderExchangePersistenceTests
REM  needs a real database and would otherwise SKIP - and a skipped test that
REM  reports "Passed!" is exactly the false green this whole exercise exists to
REM  stop. The script refuses to run without a _tests connection string, and
REM  refuses ai_investment outright.
REM
REM  No migration is applied. No provider is contacted: the connectors are off
REM  and the tests use canned bytes.
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
  "if ($cs -notmatch '(?i)(^|;)\s*database\s*=\s*[^;]*_tests\s*(;|$)') { Write-Host 'STOPPING. Not a _tests database.'; exit 4 };" ^
  "if ($cs -match '(?i)(^|;)\s*database\s*=\s*ai_investment\s*(;|$)') { Write-Host 'STOPPING. That is the evidence database.'; exit 4 };" ^
  "$env:Database__ConnectionString = $cs;" ^
  "$env:Providers__Eodhd__Enabled = 'false';" ^
  "$env:Providers__SecEdgar__Enabled = 'false';" ^
  "$app = 'tests\AI.Investment.Application.UnitTests\AI.Investment.Application.UnitTests.csproj';" ^
  "$int = 'tests\AI.Investment.Integration.Tests\AI.Investment.Integration.Tests.csproj';" ^
  "Write-Host '--- 1/4  build the two affected test projects';" ^
  "dotnet build $app -c Release --nologo;" ^
  "if ($LASTEXITCODE -ne 0) { Write-Host 'BUILD FAILED: Application.UnitTests'; exit 10 };" ^
  "dotnet build $int -c Release --nologo;" ^
  "if ($LASTEXITCODE -ne 0) { Write-Host 'BUILD FAILED: Integration.Tests'; exit 11 };" ^
  "Write-Host '--- 2/4  focused ProviderExchange provenance tests';" ^
  "dotnet test $app -c Release --nologo --filter 'FullyQualifiedName~ProviderExchangeProvenance';" ^
  "if ($LASTEXITCODE -ne 0) { exit 12 };" ^
  "Write-Host '--- 3/4  focused IngestionGateway failure-path tests';" ^
  "dotnet test $app -c Release --nologo --filter 'FullyQualifiedName~IngestionGatewayFailurePath';" ^
  "if ($LASTEXITCODE -ne 0) { exit 13 };" ^
  "Write-Host '--- 4/4  ProviderExchange persistence tests (real PostgreSQL)';" ^
  "dotnet test $int -c Release --nologo --filter 'FullyQualifiedName~ProviderExchangePersistence';" ^
  "if ($LASTEXITCODE -ne 0) { exit 14 };" ^
  "exit 0" > "artifacts\verify\provenance-verify.log" 2>&1

set EXITCODE=%ERRORLEVEL%
echo.
echo Exit code: %EXITCODE%
echo Log: artifacts\verify\provenance-verify.log
echo.
pause
endlocal
