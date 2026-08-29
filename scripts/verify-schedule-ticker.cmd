@echo off
setlocal
rem SCHEDULE TICKER BLOCK - Release build + tests.
rem Also covers the watch-disablement block, whose build had not yet been run.
rem Creates no watch, enables no cycles, makes no EODHD request, starts no API host.

cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"

echo.
echo =============================================================
echo  SCHEDULE TICKER + WATCH DISABLEMENT - RELEASE BUILD + TESTS
echo =============================================================
echo.

echo [1/5] dotnet build -c Release  (TreatWarningsAsErrors)
dotnet build "AI-Investment-Analyst.sln" -c Release --nologo > "artifacts\verify\schedule-build.log" 2>&1
set BUILD=%ERRORLEVEL%
echo       exit=%BUILD%
if not "%BUILD%"=="0" (
  echo.
  echo BUILD FAILED. Errors:
  powershell -NoProfile -Command "Select-String -Path 'artifacts\verify\schedule-build.log' -Pattern 'error|Error\(s\)' | Select-Object -First 25 | ForEach-Object { Write-Host ('  ' + $_.Line.Trim()) }"
  goto :done
)

echo [2/5] ScheduleTickerTests
dotnet test "tests\AI.Investment.Application.UnitTests\AI.Investment.Application.UnitTests.csproj" -c Release --no-build --nologo --filter "FullyQualifiedName~ScheduleTickerTests" > "artifacts\verify\schedule-test-ticker.log" 2>&1
echo       exit=%ERRORLEVEL%

echo [3/5] TriggerEvaluatorTests + OperatorConsoleTests
dotnet test "tests\AI.Investment.Application.UnitTests\AI.Investment.Application.UnitTests.csproj" -c Release --no-build --nologo --filter "FullyQualifiedName~TriggerEvaluatorTests|FullyQualifiedName~OperatorConsoleTests" > "artifacts\verify\schedule-test-console.log" 2>&1
echo       exit=%ERRORLEVEL%

echo [4/5] Full Application unit test project
dotnet test "tests\AI.Investment.Application.UnitTests\AI.Investment.Application.UnitTests.csproj" -c Release --no-build --nologo > "artifacts\verify\schedule-test-application.log" 2>&1
echo       exit=%ERRORLEVEL%

echo [5/5] Api tests + Architecture tests + Safety tests
dotnet test "tests\AI.Investment.Api.Tests\AI.Investment.Api.Tests.csproj" -c Release --no-build --nologo > "artifacts\verify\schedule-test-api.log" 2>&1
echo       exit=%ERRORLEVEL%
dotnet test "tests\AI.Investment.Architecture.Tests\AI.Investment.Architecture.Tests.csproj" -c Release --no-build --nologo > "artifacts\verify\schedule-test-arch.log" 2>&1
echo       exit=%ERRORLEVEL%
dotnet test "tests\AI.Investment.Safety.Tests\AI.Investment.Safety.Tests.csproj" -c Release --no-build --nologo > "artifacts\verify\schedule-test-safety.log" 2>&1
echo       exit=%ERRORLEVEL%

echo.
echo ---- SUMMARY ----
powershell -NoProfile -Command "Get-ChildItem 'artifacts\verify\schedule-test-*.log' | ForEach-Object { Write-Host ''; Write-Host $_.Name -ForegroundColor Cyan; $hit = Select-String -Path $_.FullName -Pattern 'Passed!|Failed!|error CS|error CA|Test Run' | Select-Object -First 6; if ($hit) { $hit | ForEach-Object { Write-Host ('  ' + $_.Line.Trim()) } } else { Write-Host '  (no summary line found)' } }"

:done
echo.
echo Logs in artifacts\verify\
echo.
pause
endlocal
