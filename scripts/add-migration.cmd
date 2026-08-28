@echo off
REM ---------------------------------------------------------------------------
REM  Double-clickable EF Core migration generator.
REM
REM  Adds the migration named below against AI.Investment.Infrastructure, using
REM  the repository's DesignTimeDbContextFactory so the tooling resolves the same
REM  connection string the application does.
REM
REM  The migration NAME is edited here rather than passed as an argument, because
REM  a double-click cannot carry one. That is deliberate: the name of a migration
REM  is part of the change, and it belongs in a file that is reviewed with it.
REM
REM  Everything it writes lands in artifacts\verify, which .gitignore excludes.
REM ---------------------------------------------------------------------------
setlocal
set MIGRATION_NAME=Phase5OpportunityApprovalCapital
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

dotnet ef migrations add %MIGRATION_NAME% ^
  --project src\AI.Investment.Infrastructure ^
  --startup-project src\AI.Investment.Api ^
  --context AppDbContext ^
  --output-dir Persistence\Migrations >> "artifacts\verify\migration.log" 2>&1

>> "artifacts\verify\migration.log" echo [migration] finished exit=%ERRORLEVEL% %DATE% %TIME%
> "artifacts\verify\MIGRATION-DONE.txt" echo exit=%ERRORLEVEL%
endlocal
