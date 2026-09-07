@echo off
setlocal
rem READ-ONLY REMEDIATION AND DESIGN.
rem   1. proves the companyfacts de-duplication fix against the ALREADY-ARCHIVED payloads
rem   2. builds the dead-name ticker mapping report from local evidence only
rem   3. analyses gate 2 against the archived cross-sections, at its existing 5% threshold
rem
rem   NO provider calls - the EDGAR connector is deliberately NOT switched on, so this run
rem   cannot reach the SEC, and no EODHD client is constructed. NO acquisition. NO strategy
rem   declarations. NO scoring. NO writes: observation, run, quarantine and opportunity counts
rem   are asserted unchanged. NO threshold is changed.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  UNIVERSE REMEDIATION - READ ONLY
echo   1. Release build, warnings are errors
echo   2. normalisation fix proved on archived payloads
echo   3. dead-name ticker mapping from local evidence
echo   4. gate 2 analysed at its existing 5%% threshold
echo   5. full Release suite
echo   NO SEC calls. NO EODHD calls. NO writes. NO acquisition.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\remediation-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\remediation-build.log"
  goto :done
)
echo   PASS  Release build
echo.

echo --- focused: the normaliser and its existing regressions
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~Normaliz|FullyQualifiedName~SecEdgar|FullyQualifiedName~QuarterlyCadenceTests" -LogName "remediation-focused.log" -Label "focused: normalisation and EDGAR facts"
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" goto :done

set AIINV_UNIVERSE_REMEDIATE=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~UniverseRemediationTests" -LogName "remediation-stage.log" -Label "read-only: dedup proof, ticker mapping, gate 2 analysis"
set EXITCODE=%ERRORLEVEL%
set AIINV_UNIVERSE_REMEDIATE=
if not "%EXITCODE%"=="0" goto :done

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~AI.Investment" -LogName "remediation-full.log" -Label "full Release suite"
set EXITCODE=%ERRORLEVEL%

echo.
echo --- What was found
powershell -NoProfile -ExecutionPolicy Bypass -Command "foreach ($f in 'remediation-normalisation.md','remediation-gate2.md') { $p = Join-Path 'artifacts\verify' $f; if (Test-Path $p) { Write-Host ''; Write-Host ('  == ' + $f); Get-Content $p | Where-Object { $_ -match 'Duplicates within|Duplicate rows in|invents|loses|PASS|FAIL|passes|fails|^\| ' } | Select-Object -First 30 | ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) } } }"

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Reports: artifacts\verify\remediation-normalisation.md
echo          artifacts\verify\remediation-tickers.md
echo          artifacts\verify\remediation-gate2.md
echo.
pause
endlocal
