@echo off
setlocal
rem EXACTLY ONE EODHD CALL. Read-only: the account endpoint, which reports the plan.
rem NO price data. NO backfill. NO ingestion, archive or ledger write. The token is never printed.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  EODHD SUBSCRIPTION - VERIFICATION
echo   1. Release build, warnings are errors
echo   2. ONE call to the account endpoint
echo   NO price data. NO backfill. NO universe fetch. NO writes.
echo   The client refuses a second request.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\subscription-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\subscription-build.log"
  goto :done
)
echo   PASS  Release build
echo.

set AIINV_SUBSCRIPTION=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~SubscriptionVerificationTests" -LogName "subscription.log" -Label "one call: which subscription is this token on"
set EXITCODE=%ERRORLEVEL%
set AIINV_SUBSCRIPTION=

echo.
echo --- What the account reports
powershell -NoProfile -ExecutionPolicy Bypass -Command "if (Test-Path 'artifacts\verify\subscription.md') { Get-Content 'artifacts\verify\subscription.md' | Where-Object { $_ -match '^\| |^\*\*|^- ' } | ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) } }"

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Artifact: artifacts\verify\subscription.md  (plan and limits - no credential, no personal data)
echo.
pause
endlocal
