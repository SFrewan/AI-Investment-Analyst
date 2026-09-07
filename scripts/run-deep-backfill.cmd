@echo off
setlocal
rem THE FIVE-YEAR ACQUISITION. Up to 40 real, billable EODHD calls.
rem 20 instruments x (corporate actions + prices), 2021-09-01 to 2026-08-31.
rem Every request through the action gateway, the ledger asked before each fetch.
rem NO paper trading. NO execution. NO watches created. The token is never printed.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  FIVE-YEAR PRICE ACQUISITION
echo   1. Release build, warnings are errors
echo   2. the 40 planned requests, issued through the seam
echo   3. the plan's seven checks, over the stored rows
echo   NO paper trading. NO orders. NO watches. NO bypassed controls.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\deep-backfill-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\deep-backfill-build.log"
  goto :done
)
echo   PASS  Release build
echo.

set AIINV_DEEP_BACKFILL=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~DeepHistoryAcquisitionTests" -LogName "deep-backfill.log" -Label "five-year acquisition: 40 requests through the seam"
set EXITCODE=%ERRORLEVEL%
set AIINV_DEEP_BACKFILL=

echo.
echo --- The plan's seven checks
powershell -NoProfile -ExecutionPolicy Bypass -Command "if (Test-Path 'artifacts\verify\deep-backfill.md') { Get-Content 'artifacts\verify\deep-backfill.md' | Where-Object { $_ -match 'PASS|FAIL|billable|^\| Ingestion runs|^\| Observations|^\| Quarantined|^\| Opportunities' } | ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*]', '')) } }"

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Artifact: artifacts\verify\deep-backfill.md
echo.
echo Run this a second time to prove idempotency: every request should report
echo "already complete, skipped" and the billable call count should be zero.
echo.
pause
endlocal
