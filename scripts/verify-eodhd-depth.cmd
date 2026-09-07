@echo off
setlocal
rem EXACTLY ONE EODHD CALL. One instrument, one month at the far edge of the intended window.
rem NO backfill. NO universe fetch. NO ingestion, archive or ledger write. NO prices recorded.
rem The client refuses a second request. The token is never printed.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  EODHD HISTORICAL DEPTH - VERIFICATION
echo   1. Release build, warnings are errors
echo   2. ONE call: AAPL.US, 2021-09-01 to 2021-09-30
echo   NO backfill. NO universe. NO writes. NO prices recorded.
echo   The client refuses a second request.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\depth-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\depth-build.log"
  goto :done
)
echo   PASS  Release build
echo.

set AIINV_DEPTH=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~HistoricalDepthVerificationTests" -LogName "depth.log" -Label "one call: does the account carry the far edge of the window"
set EXITCODE=%ERRORLEVEL%
set AIINV_DEPTH=

echo.
echo --- What came back
powershell -NoProfile -ExecutionPolicy Bypass -Command "if (Test-Path 'artifacts\verify\depth.md') { Get-Content 'artifacts\verify\depth.md' | Where-Object { $_ -match '^\| |^\*\*|^- ' } | ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) } }"

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Artifact: artifacts\verify\depth.md  (session count and date range - no prices, no credential)
echo.
pause
endlocal
