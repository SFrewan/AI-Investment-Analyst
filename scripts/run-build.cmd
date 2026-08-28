@echo off
REM ---------------------------------------------------------------------------
REM  Double-clickable build-only launcher: verify.ps1 with -SkipTests.
REM
REM  The full pipeline is the gate; this is the fast inner loop. A compile error
REM  is found in about twenty seconds instead of a minute, which matters when the
REM  only way to start a process on this machine is to click a file.
REM
REM  Everything it writes lands in artifacts\verify, which .gitignore excludes.
REM ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"
> "artifacts\verify\launcher.log" echo [launcher] build-only started %DATE% %TIME% in "%CD%"
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\verify.ps1" -SkipTests >> "artifacts\verify\launcher.log" 2>&1
>> "artifacts\verify\launcher.log" echo [launcher] finished exit=%ERRORLEVEL% %DATE% %TIME%
endlocal
