@echo off
setlocal
rem READ-ONLY. Confirms the EODHD credential reaches the composition the acquisition runs under.
rem NO provider calls. NO purchases. NO backfill. The token's value is never printed.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  EODHD CREDENTIAL - PRESENCE CHECK
echo   1. Release build, warnings are errors
echo   2. credential hygiene: no token in any tracked settings file
echo   3. presence: does the operator's token reach the composition
echo   NO provider calls. NO backfill. The value is never printed.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\credential-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\credential-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~EodhdCredentialSourcingTests|FullyQualifiedName~WindowsUserEnvironmentTests|FullyQualifiedName~EodhdConfigurationTests" -LogName "credential-sourcing.log" -Label "credential sourcing: names, binding, stored-vs-inherited, refusals"
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" goto :done

set AIINV_CREDENTIAL=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~CredentialHygieneTests" -LogName "credential-presence.log" -Label "presence: the token reaches the acquisition composition"
set EXITCODE=%ERRORLEVEL%
set AIINV_CREDENTIAL=

echo.
echo --- What was found
powershell -NoProfile -ExecutionPolicy Bypass -Command "if (Test-Path 'artifacts\verify\credential.md') { Get-Content 'artifacts\verify\credential.md' | Where-Object { $_ -match '^\| [A-Z]|^\*\*' } | ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) } }"

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Artifact: artifacts\verify\credential.md  (presence, length, fingerprint - no value)
echo.
if not "%EXITCODE%"=="0" (
  echo If the check says no credential arrived: a variable set in another window is not
  echo visible to a process that was already running. Close every terminal and editor
  echo opened before you set it, then run this again.
  echo.
)
pause
endlocal
