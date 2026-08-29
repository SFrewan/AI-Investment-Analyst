@echo off
setlocal
rem DATABASE PROVENANCE INVESTIGATION. READ ONLY.
rem Applies no migration. Creates no table. Alters no schema. Writes no row.
rem Does not touch RunCycles. Makes no EODHD request.
cd /d "%~dp0.."
echo.
echo ===============================================================
echo  DATABASE PROVENANCE INVESTIGATION - READ ONLY
echo.
echo  Answers: what the API is really connected to, whether the EF
echo  migrations history exists (the earlier check was WRONG), what
echo  tables exist, what data is there, and exactly what applying
echo  the pending migrations would do.
echo.
echo  Nothing is applied. An idempotent SQL script is GENERATED to
echo  artifacts\verify\pending-migration.sql for you to read - it
echo  is not run against the database.
echo ===============================================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0investigate-database.ps1"
echo.
pause
endlocal
