@echo off
setlocal
rem STAGE: store repair, one delisted-list call, and the systematic-sample manifest.
rem
rem   1. remove the duplicate observation rows (a WRITE, through the Action/Policy seam)
rem   2. ONE billable EODHD call for the US delisted symbol list, cached whole
rem   3. seal the systematic 1-in-N sample manifest and judge every applicable gate
rem
rem   NO SEC calls - the EDGAR connector is deliberately NOT switched on.
rem   NO price or corporate-action acquisition. NO strategy declarations. NO scoring.
rem   NO threshold is changed: gate 2 stays at 5%% and gate 12 at zero tolerance.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  REPAIR, DELISTED MAPPING, SYSTEMATIC-SAMPLE MANIFEST
echo   1. Release build, warnings are errors
echo   2. remove duplicate observation rows (write, through the seam)
echo   3. ONE billable EODHD delisted-symbol-list call, cached
echo   4. seal the systematic sample and judge the gates
echo   5. full Release suite
echo   NO SEC calls. NO prices. NO declarations. NO scoring.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\repair-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\repair-build.log"
  goto :done
)
echo   PASS  Release build
echo.

set AIINV_UNIVERSE_REPAIR=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~UniverseRepairTests" -LogName "repair-stage.log" -Label "1/3 remove duplicate observation rows"
set EXITCODE=%ERRORLEVEL%
set AIINV_UNIVERSE_REPAIR=
if not "%EXITCODE%"=="0" goto :done

set AIINV_UNIVERSE_DELISTED=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~UniverseDelistedListTests" -LogName "delisted-list.log" -Label "2/3 one billable delisted-symbol-list call"
set EXITCODE=%ERRORLEVEL%
set AIINV_UNIVERSE_DELISTED=
if not "%EXITCODE%"=="0" goto :done

set AIINV_UNIVERSE_REMEDIATE=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~UniverseRemediationTests" -LogName "remediation-rerun.log" -Label "2b/3 dropout ticker matching against the cached list"
set EXITCODE=%ERRORLEVEL%
set AIINV_UNIVERSE_REMEDIATE=
if not "%EXITCODE%"=="0" goto :done

set AIINV_UNIVERSE_SAMPLE=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~UniverseSampleManifestTests" -LogName "sample-manifest.log" -Label "3/3 seal the systematic sample, judge the gates"
set EXITCODE=%ERRORLEVEL%
set AIINV_UNIVERSE_SAMPLE=
if not "%EXITCODE%"=="0" goto :done

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~AI.Investment" -LogName "repair-full.log" -Label "full Release suite"
set EXITCODE=%ERRORLEVEL%

echo.
echo --- What was found
powershell -NoProfile -ExecutionPolicy Bypass -Command "foreach ($f in 'repair-observations.md','delisted-list.md','universe-sample400.md') { $p = Join-Path 'artifacts\verify' $f; if (Test-Path $p) { Write-Host ''; Write-Host ('  == ' + $f); Get-Content $p | Where-Object { $_ -match 'Duplicate rows|Rows removed|Requests sent|Symbols cached|fingerprint|Members sealed|Unmatched|PASS|FAIL|deferred|Population|Sampling' } | Select-Object -First 28 | ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) } } }"

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Manifest: declarations\universe-sample400-2021-2026.json
echo Reports:  artifacts\verify\repair-observations.md
echo           artifacts\verify\delisted-list.md
echo           artifacts\verify\remediation-tickers.md
echo           artifacts\verify\universe-sample400.md
echo.
pause
endlocal
