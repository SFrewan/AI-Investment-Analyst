@echo off
setlocal
rem EMPTY-SERIES EVIDENCE CENSUS. READ ONLY.
rem   LGIQ NGM ONEM QUMU SDC SHPW
rem   No replay. No provider. No acquisition. No authorisation. No database write.
rem   Gate 6, the sealed artifact, SplitAdjustment and [] semantics are untouched.
rem   Exit 0 = READY FOR ONE-BY-ONE REPLAY.  Non-zero = BLOCKED or unexpected state.

cd /d "%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-empty-census.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
