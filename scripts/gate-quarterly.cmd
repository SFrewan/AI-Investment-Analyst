@echo off
setlocal
rem READ-ONLY. Re-reads archived EDGAR payloads with the quarterly filter open.
rem NO provider calls. NO rows written. NO acquisition. NO declarations.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  QUARTERLY NORMALISATION - STEP 1 OF 2
echo   1. Release build, warnings are errors
echo   2. focused: normalisation, EDGAR facts, cadence reasoning
echo   3. archived payloads re-read, nothing written
echo   4. full Release suite
echo   NO provider calls. NO writes. NO acquisition.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\quarterly-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\quarterly-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~SecEdgar|FullyQualifiedName~Normaliz|FullyQualifiedName~QuarterlyCadenceTests" -LogName "quarterly-focused.log" -Label "focused: normalisation, EDGAR facts, cadence reasoning"
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" goto :done

set AIINV_QUARTERLY=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~QuarterlyNormalisationTests" -LogName "quarterly-stage.log" -Label "archived payloads re-read, nothing written"
set EXITCODE=%ERRORLEVEL%
set AIINV_QUARTERLY=
if not "%EXITCODE%"=="0" goto :done

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~AI.Investment" -LogName "quarterly-full.log" -Label "full Release suite"
set EXITCODE=%ERRORLEVEL%

echo.
echo --- What was found
powershell -NoProfile -ExecutionPolicy Bypass -Command "if (Test-Path 'artifacts\verify\quarterly.md') { Get-Content 'artifacts\verify\quarterly.md' | Where-Object { $_ -match '^\| [A-Za-z`]' } | ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) } }"

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Artifact: artifacts\verify\quarterly.md
echo.
pause
endlocal
