@echo off
setlocal
rem WATCH REVERSIBILITY BLOCK - build and test.
rem Read-only with respect to the running system: builds, runs tests, writes logs.
rem Creates no watch, enables no cycles, makes no EODHD request.

cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"

echo.
echo =============================================================
echo  WATCH REVERSIBILITY BLOCK - RELEASE BUILD + TESTS
echo =============================================================
echo.

echo [1/3] dotnet build -c Release  (TreatWarningsAsErrors)
dotnet build "AI-Investment-Analyst.sln" -c Release --nologo > "artifacts\verify\watch-build.log" 2>&1
set BUILD=%ERRORLEVEL%
echo       exit=%BUILD%
if not "%BUILD%"=="0" (
  echo.
  echo BUILD FAILED. Last 40 lines:
  powershell -NoProfile -Command "Get-Content 'artifacts\verify\watch-build.log' -Tail 40"
  goto :done
)

echo [2/3] Application unit tests - OperatorConsoleTests
dotnet test "tests\AI.Investment.Application.UnitTests\AI.Investment.Application.UnitTests.csproj" -c Release --no-build --nologo --filter "FullyQualifiedName~OperatorConsoleTests" > "artifacts\verify\watch-test-console.log" 2>&1
set T1=%ERRORLEVEL%
echo       exit=%T1%

echo [3/3] Api tests + Architecture tests
dotnet test "tests\AI.Investment.Api.Tests\AI.Investment.Api.Tests.csproj" -c Release --no-build --nologo > "artifacts\verify\watch-test-api.log" 2>&1
set T2=%ERRORLEVEL%
echo       exit=%T2%
dotnet test "tests\AI.Investment.Architecture.Tests\AI.Investment.Architecture.Tests.csproj" -c Release --no-build --nologo > "artifacts\verify\watch-test-arch.log" 2>&1
set T3=%ERRORLEVEL%
echo       exit=%T3%

echo.
echo ---- SUMMARY ----
powershell -NoProfile -Command "Get-ChildItem 'artifacts\verify\watch-test-*.log' | ForEach-Object { $n=$_.Name; $l=(Select-String -Path $_.FullName -Pattern 'Passed!|Failed!|error' | Select-Object -First 3); Write-Host ''; Write-Host $n -ForegroundColor Cyan; $l | ForEach-Object { Write-Host ('  ' + $_.Line.Trim()) } }"

:done
echo.
echo Logs in artifacts\verify\
echo.
pause
endlocal
