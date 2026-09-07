@echo off
setlocal
rem READ-ONLY apart from the manifest it seals. NO provider calls. NO acquisition.
rem NO strategy declarations. NO scoring. Observation and run counts asserted unchanged.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  POINT-IN-TIME UNIVERSE MANIFEST
echo   1. Release build, warnings are errors
echo   2. seal the manifest and judge the twelve gates
echo   3. full Release suite
echo   NO provider calls. NO acquisition. NO declarations. NO scoring.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\manifest-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\manifest-build.log"
  goto :done
)
echo   PASS  Release build
echo.

set AIINV_MANIFEST=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~UniverseManifestTests" -LogName "manifest-stage.log" -Label "seal the manifest, judge the twelve gates"
set EXITCODE=%ERRORLEVEL%
set AIINV_MANIFEST=
if not "%EXITCODE%"=="0" goto :done

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~AI.Investment" -LogName "manifest-full.log" -Label "full Release suite"
set EXITCODE=%ERRORLEVEL%

echo.
echo --- The twelve gates
powershell -NoProfile -ExecutionPolicy Bypass -Command "if (Test-Path 'artifacts\verify\universe-manifest.md') { Get-Content 'artifacts\verify\universe-manifest.md' | Where-Object { $_ -match 'PASS|FAIL|deferred|fingerprint|Unmatched, recorded|Members sealed' } | ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) } }"

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Manifest: declarations\universe-pilot-2021-2026.json
echo Report:   artifacts\verify\universe-manifest.md
echo.
pause
endlocal
