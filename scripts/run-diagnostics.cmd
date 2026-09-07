@echo off
setlocal
rem READ-ONLY DIAGNOSTICS.
rem   Answers two questions from what is already stored: where the duplicate facts came from,
rem   and whether the seventeen dead companies can be priced from local evidence.
rem
rem   NO provider calls - the EDGAR connector is deliberately NOT switched on, so this run
rem   cannot reach the SEC even if something asked it to. NO EODHD calls. NO acquisition.
rem   NO writes: observation, run, quarantine and opportunity counts are asserted unchanged.
rem   NO manifest, declaration or roster is modified. Gate 2 and gate 12 keep their thresholds.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  UNIVERSE DIAGNOSTICS - READ ONLY
echo   1. Release build, warnings are errors
echo   2. duplicate-fact provenance and dropout ticker evidence
echo   3. full Release suite
echo   NO SEC calls. NO EODHD calls. NO writes. NO acquisition.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\diagnostics-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\diagnostics-build.log"
  goto :done
)
echo   PASS  Release build
echo.

set AIINV_UNIVERSE_DIAGNOSE=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~UniverseDiagnosticsTests" -LogName "universe-diagnostics.log" -Label "read-only: duplicate provenance and dropout ticker evidence"
set EXITCODE=%ERRORLEVEL%
set AIINV_UNIVERSE_DIAGNOSE=
if not "%EXITCODE%"=="0" goto :done

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~AI.Investment" -LogName "diagnostics-full.log" -Label "full Release suite"
set EXITCODE=%ERRORLEVEL%

echo.
echo --- What was found
powershell -NoProfile -ExecutionPolicy Bypass -Command "if (Test-Path 'artifacts\verify\universe-diagnostics.md') { Get-Content 'artifacts\verify\universe-diagnostics.md' | Where-Object { $_ -match 'Duplicate groups|above one per fact|Companies affected|pilot companies|other |dropouts have a ticker|^\| Wholly|^\| Straddling|^\| This stage' } | ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) } }"

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Report: artifacts\verify\universe-diagnostics.md
echo.
pause
endlocal
