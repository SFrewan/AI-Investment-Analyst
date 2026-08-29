@echo off
setlocal enabledelayedexpansion
rem PROVIDER-FAILURE ESCALATION BLOCK - Release build + tests.
rem Offline only: no API host, no EODHD request, no watch, no cycle, RunCycles untouched.

cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"

echo.
echo =============================================================
echo  PROVIDER-FAILURE ESCALATION - RELEASE BUILD + TESTS
echo  No live EODHD request. No cycle. No RunCycles change.
echo =============================================================
echo.

echo [1/4] dotnet build -c Release  (TreatWarningsAsErrors)
dotnet build "AI-Investment-Analyst.sln" -c Release --nologo > "artifacts\verify\providerfail-build.log" 2>&1
set BUILD=%ERRORLEVEL%
echo       exit=%BUILD%
if not "%BUILD%"=="0" (
  echo.
  echo BUILD FAILED. Errors:
  powershell -NoProfile -Command "Select-String -Path 'artifacts\verify\providerfail-build.log' -Pattern ': error ' | Select-Object -First 25 | ForEach-Object { Write-Host ('  ' + $_.Line.Trim()) }"
  goto :done
)

echo [2/4] OperatingCycleRunnerTests
dotnet test "tests\AI.Investment.Application.UnitTests\AI.Investment.Application.UnitTests.csproj" -c Release --no-build --nologo --filter "FullyQualifiedName~OperatingCycleRunnerTests" > "artifacts\verify\providerfail-test-runner.log" 2>&1
echo       exit=%ERRORLEVEL%

echo [3/4] Full Application unit test project
dotnet test "tests\AI.Investment.Application.UnitTests\AI.Investment.Application.UnitTests.csproj" -c Release --no-build --nologo > "artifacts\verify\providerfail-test-application.log" 2>&1
echo       exit=%ERRORLEVEL%

echo [4/4] Domain, Api, Architecture, Safety
for %%P in (Domain.UnitTests Api.Tests Architecture.Tests Safety.Tests) do (
  dotnet test "tests\AI.Investment.%%P\AI.Investment.%%P.csproj" -c Release --no-build --nologo > "artifacts\verify\providerfail-test-%%P.log" 2>&1
  echo       %%P exit=!ERRORLEVEL!
)

echo.
echo ---- SUMMARY ----
powershell -NoProfile -Command "Get-ChildItem 'artifacts\verify\providerfail-test-*.log' | ForEach-Object { Write-Host ''; Write-Host $_.Name -ForegroundColor Cyan; $h = Select-String -Path $_.FullName -Pattern 'Passed!|Failed!|error ' | Select-Object -First 6; if ($h) { $h | ForEach-Object { Write-Host ('  ' + $_.Line.Trim()) } } else { Write-Host '  (no summary line)' } }"

:done
echo.
echo Logs in artifacts\verify\
echo.
pause
endlocal
