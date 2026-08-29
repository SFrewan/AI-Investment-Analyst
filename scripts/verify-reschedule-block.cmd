@echo off
setlocal enabledelayedexpansion
rem RESCHEDULE BLOCK - Release build + tests. Offline.
rem Starts no API, makes no EODHD request, changes no database, touches no watch.
cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"
echo.
echo  WATCH RESCHEDULE - RELEASE BUILD + TESTS (offline)
echo.
echo [1/3] dotnet build -c Release (TreatWarningsAsErrors)
dotnet build "AI-Investment-Analyst.sln" -c Release --nologo > "artifacts\verify\reschedule-build.log" 2>&1
set BUILD=%ERRORLEVEL%
echo       exit=%BUILD%
if not "%BUILD%"=="0" (
  powershell -NoProfile -Command "Select-String -Path 'artifacts\verify\reschedule-build.log' -Pattern ': error ' | Select-Object -First 30 | ForEach-Object { Write-Host ('  ' + $_.Line.Trim()) }"
  goto :done
)
echo [2/3] Focused: ScheduleTicker, TriggerEvaluator, OperatorConsole, OperatingCycleRunner
dotnet test "tests\AI.Investment.Application.UnitTests\AI.Investment.Application.UnitTests.csproj" -c Release --no-build --nologo --filter "FullyQualifiedName~ScheduleTickerTests|FullyQualifiedName~TriggerEvaluatorTests|FullyQualifiedName~OperatorConsoleTests|FullyQualifiedName~OperatingCycleRunnerTests" > "artifacts\verify\reschedule-test-focused.log" 2>&1
echo       exit=%ERRORLEVEL%
echo [3/3] Full Application, then Domain, Api, Architecture, Safety
dotnet test "tests\AI.Investment.Application.UnitTests\AI.Investment.Application.UnitTests.csproj" -c Release --no-build --nologo > "artifacts\verify\reschedule-test-application.log" 2>&1
echo       Application exit=%ERRORLEVEL%
for %%P in (Domain.UnitTests Api.Tests Architecture.Tests Safety.Tests) do (
  dotnet test "tests\AI.Investment.%%P\AI.Investment.%%P.csproj" -c Release --no-build --nologo > "artifacts\verify\reschedule-test-%%P.log" 2>&1
  echo       %%P exit=!ERRORLEVEL!
)
echo.
echo ---- SUMMARY ----
powershell -NoProfile -Command "Get-ChildItem 'artifacts\verify\reschedule-test-*.log' | ForEach-Object { Write-Host ''; Write-Host $_.Name -ForegroundColor Cyan; $h = Select-String -Path $_.FullName -Pattern 'Passed!|Failed!|error ' | Select-Object -First 6; if ($h) { $h | ForEach-Object { Write-Host ('  ' + $_.Line.Trim()) } } else { Write-Host '  (no summary line)' } }"
:done
echo.
pause
endlocal
