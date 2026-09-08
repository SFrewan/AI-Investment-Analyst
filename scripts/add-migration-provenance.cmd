@echo off
REM ---------------------------------------------------------------------------
REM  EF Core migration generator for the per-run provider-exchange provenance table.
REM
REM  A parallel of scripts\add-migration.cmd rather than an edit of it: that file
REM  names the migration it produced, and the name of a migration is part of the
REM  change it belongs to. Overwriting it would erase which change it made.
REM
REM  ADDITIVE ONLY. This scaffolds one new table and its indexes. It changes no
REM  existing table and backfills nothing - the evidence it records can only be
REM  captured at request time, so there is nothing to backfill.
REM
REM  Scaffolding needs a well-formed connection string, not a reachable server:
REM  nothing here connects to anything and nothing is applied.
REM
REM  WHICH DATABASE. Not decided here any more. scripts\Resolve-ScaffoldTarget.ps1
REM  decides it, the same way for every scaffolding script in this repository, and
REM  it prints what it decided before the tool runs. The rules it applies:
REM
REM    * AIINV_TEST_POSTGRES is the default and the only default.
REM    * AIINV_DESIGNTIME_DB is used only when AIINV_SCAFFOLD_TARGET=designtime.
REM      There is no silent fallback: an empty variable is a stop, not a reason to
REM      quietly use the other one. This file used to have that fallback, and on
REM      this machine AIINV_DESIGNTIME_DB names ai_investment.
REM    * A connection naming ai_investment is refused - whichever variable carried
REM      it - unless AIINV_SCAFFOLD_ALLOW_EVIDENCE_DB carries the exact opt-in
REM      token the resolver documents.
REM
REM  The apply-time guard in scripts\run-provenance-migrate.ps1 is a separate and
REM  stricter thing, and this change does not touch it.
REM ---------------------------------------------------------------------------
setlocal
set MIGRATION_NAME=ProviderExchangeProvenance
cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"
> "artifacts\verify\migration-provenance.log" echo [migration] %MIGRATION_NAME% started %DATE% %TIME% in "%CD%"

if not exist ".config\dotnet-tools.json" (
  >> "artifacts\verify\migration-provenance.log" echo [migration] creating local tool manifest
  dotnet new tool-manifest >> "artifacts\verify\migration-provenance.log" 2>&1
  dotnet tool install dotnet-ef --version 8.0.10 >> "artifacts\verify\migration-provenance.log" 2>&1
)

dotnet tool restore >> "artifacts\verify\migration-provenance.log" 2>&1

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$scripts = Join-Path (Get-Location) 'scripts';" ^
  "$local = Join-Path $scripts 'verify.local.ps1';" ^
  "if (Test-Path -Path $local) { . $local };" ^
  ". (Join-Path $scripts 'Resolve-ScaffoldTarget.ps1');" ^
  "$t = Resolve-ScaffoldTarget;" ^
  "Set-Content -Path 'artifacts\verify\scaffold-target.txt' -Value $t.Report -Encoding UTF8;" ^
  "Write-Host $t.Report;" ^
  "if (-not $t.Allowed) { Write-Host '[migration] REFUSED - nothing was scaffolded.'; exit $t.ExitCode };" ^
  "$env:Database__ConnectionString = $t.ConnectionString;" ^
  "dotnet ef migrations add %MIGRATION_NAME% --project src\AI.Investment.Infrastructure --startup-project src\AI.Investment.Api --context AppDbContext --output-dir Persistence\Migrations --configuration Release;" ^
  "exit $LASTEXITCODE" >> "artifacts\verify\migration-provenance.log" 2>&1

set EXITCODE=%ERRORLEVEL%
>> "artifacts\verify\migration-provenance.log" echo [migration] finished exit=%EXITCODE% %DATE% %TIME%

echo.
if exist "artifacts\verify\scaffold-target.txt" type "artifacts\verify\scaffold-target.txt"
echo.
echo Migration scaffolding exit code: %EXITCODE%
echo Log: artifacts\verify\migration-provenance.log
echo.
if not "%EXITCODE%"=="0" (
  echo REFUSED or FAILED. What the codes mean:
  echo    2 = no connection string is set for the requested target
  echo    4 = refused - that is the evidence database and no opt-in was supplied
  echo    5 = the connection string does not name a database
  echo    6 = AIINV_SCAFFOLD_TARGET is neither 'test' nor 'designtime'
  echo.
)
echo NOTHING WAS APPLIED. This only generated the migration files.
echo.
pause
endlocal
