@echo off
setlocal
rem EXACTLY ONE EODHD CALL. One delisted US ticker, one month it certainly traded.
rem NO universe acquisition. NO ingestion, archive or ledger write. NO prices recorded.
rem The client refuses a second request. The token is never printed.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  DELISTED HISTORY - STEP 2 OF 2
echo   1. Release build, warnings are errors
echo   2. ONE call: ATVI.US, 2023-09-01 to 2023-09-29
echo   ATVI delisted October 2023 (Microsoft acquisition).
echo   NO acquisition. NO writes. The client refuses a second request.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\delisted-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\delisted-build.log"
  goto :done
)
echo   PASS  Release build
echo.

set AIINV_DELISTED=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~DelistedHistoryProbeTests" -LogName "delisted.log" -Label "one call: does a delisted ticker still have history"
set EXITCODE=%ERRORLEVEL%
set AIINV_DELISTED=

echo.
echo --- What came back
powershell -NoProfile -ExecutionPolicy Bypass -Command "if (Test-Path 'artifacts\verify\delisted.md') { Get-Content 'artifacts\verify\delisted.md' | Where-Object { $_ -match '^\| |^\*\*|^- ' } | ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) } }"

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Artifact: artifacts\verify\delisted.md  (session count and date range - no prices, no credential)
echo.
pause
endlocal
