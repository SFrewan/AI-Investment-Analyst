@echo off
REM ---------------------------------------------------------------------------
REM  The composition test on its own, with everything captured.
REM
REM  Plain cmd redirection rather than PowerShell, so nothing about how a native
REM  command's stderr is treated can truncate the log before the failure reaches
REM  it. Diagnostic only: one filtered test run, no migration, no vendor.
REM ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"

dotnet test tests\AI.Investment.Integration.Tests\AI.Investment.Integration.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ProvenanceComposition" > "artifacts\verify\composition-only.log" 2>&1

set EXITCODE=%ERRORLEVEL%
echo.
echo Exit code: %EXITCODE%
echo Log: artifacts\verify\composition-only.log
echo.
pause
endlocal
