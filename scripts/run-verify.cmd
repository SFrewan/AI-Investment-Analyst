@echo off
REM ---------------------------------------------------------------------------
REM  Double-clickable launcher for verify.ps1.
REM
REM  Exists because the agent driving this repository can read and write files
REM  here but cannot type into a terminal: it can start this by clicking it, and
REM  then read the results out of artifacts\verify.
REM
REM  Everything it writes lands in artifacts\verify, which .gitignore excludes.
REM ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"
> "artifacts\verify\launcher.log" echo [launcher] started %DATE% %TIME% in "%CD%"
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\verify.ps1" %* >> "artifacts\verify\launcher.log" 2>&1
>> "artifacts\verify\launcher.log" echo [launcher] finished exit=%ERRORLEVEL% %DATE% %TIME%
endlocal
