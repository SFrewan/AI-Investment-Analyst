@echo off
REM ---------------------------------------------------------------------------
REM  Double-clickable EF Core migration generator.
REM
REM  Adds the migration named below against AI.Investment.Infrastructure, using
REM  the API as the startup project so the tooling resolves the same composition
REM  root the application does.
REM
REM  The migration NAME is edited here rather than passed as an argument, because
REM  a double-click cannot carry one. That is deliberate: the name of a migration
REM  is part of the change, and it belongs in a file that is reviewed with it.
REM
REM  CONNECTION STRING. Since the credential was removed from tracked
REM  configuration, appsettings carries an empty one and the API's ValidateOnStart
REM  refuses to build a host without it - which the EF tooling does in order to
REM  find the DbContext. The value is therefore taken from the machine-local,
REM  git-ignored scripts\verify.local.ps1, exactly as verify.ps1 takes it, and
REM  exported as Database__ConnectionString for this process only. Scaffolding a
REM  migration needs a well-formed connection string rather than a reachable
REM  server: nothing here connects to anything.
REM
REM  WHICH DATABASE. This script used to prefer AIINV_DESIGNTIME_DB and fall back
REM  to AIINV_TEST_POSTGRES. On this machine AIINV_DESIGNTIME_DB names
REM  ai_investment - the database holding the platform's observation evidence - so
REM  the evidence database was the DEFAULT for a family of scripts whose siblings
REM  do connect. Scaffolding never connected, so nothing was harmed; a default
REM  nobody chose is still the shape a later accident takes.
REM
REM  The choice now lives in scripts\Resolve-ScaffoldTarget.ps1, is the same for
REM  every scaffolding script here, and is printed before the tool runs:
REM
REM    * AIINV_TEST_POSTGRES is the default and the only default.
REM    * AIINV_DESIGNTIME_DB is used only when AIINV_SCAFFOLD_TARGET=designtime.
REM      There is no silent fallback in either direction.
REM    * A connection naming ai_investment is refused - whichever variable carried
REM      it - unless AIINV_SCAFFOLD_ALLOW_EVIDENCE_DB carries the exact opt-in
REM      token the resolver documents.
REM
REM  CONFIGURATION. Release, deliberately. The EF tool builds Debug by default, and a locally
REM  running API holds its own Debug output open - so scaffolding a migration while the application
REM  is running failed with a file-copy error that said nothing about migrations. Release is the
REM  configuration the verify scripts already build, so the tooling reuses that output.
REM
REM  Everything it writes lands in artifacts\verify, which .gitignore excludes.
REM ---------------------------------------------------------------------------
setlocal
set MIGRATION_NAME=Block3PositionAndPortfolio
cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"
> "artifacts\verify\migration.log" echo [migration] %MIGRATION_NAME% started %DATE% %TIME% in "%CD%"

REM The EF tool is not a project dependency and may not be present. Installing it
REM into a local manifest keeps the version pinned with the repository rather than
REM depending on whatever happens to be installed globally on a given machine.
if not exist ".config\dotnet-tools.json" (
  >> "artifacts\verify\migration.log" echo [migration] creating local tool manifest
  dotnet new tool-manifest >> "artifacts\verify\migration.log" 2>&1
  dotnet tool install dotnet-ef --version 8.0.10 >> "artifacts\verify\migration.log" 2>&1
)

dotnet tool restore >> "artifacts\verify\migration.log" 2>&1

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
  "exit $LASTEXITCODE" >> "artifacts\verify\migration.log" 2>&1

REM Captured immediately. %ERRORLEVEL% expands when the line is parsed, and the echo
REM on the next line succeeds - so reading it twice used to report the echo's result
REM rather than the tool's, which would have hidden a refusal behind exit=0.
set EXITCODE=%ERRORLEVEL%

>> "artifacts\verify\migration.log" echo [migration] finished exit=%EXITCODE% %DATE% %TIME%
> "artifacts\verify\MIGRATION-DONE.txt" echo exit=%EXITCODE%

echo.
if exist "artifacts\verify\scaffold-target.txt" type "artifacts\verify\scaffold-target.txt"
echo.
echo Migration scaffolding exit code: %EXITCODE%
echo Log: artifacts\verify\migration.log
if not "%EXITCODE%"=="0" (
  echo.
  echo REFUSED or FAILED. What the codes mean:
  echo    2 = no connection string is set for the requested target
  echo    4 = refused - that is the evidence database and no opt-in was supplied
  echo    5 = the connection string does not name a database
  echo    6 = AIINV_SCAFFOLD_TARGET is neither 'test' nor 'designtime'
)
echo.
endlocal
