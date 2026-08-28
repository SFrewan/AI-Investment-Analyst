@echo off
REM Double-clickable launcher for the Phase 5 mutation-testing gate.
REM Writes artifacts\verify\mutation.log and artifacts\verify\MUTATION-DONE.txt.
cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"
> "artifacts\verify\mutation-launcher.log" echo [launcher] mutation started %DATE% %TIME%
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0mutation.ps1" >> "artifacts\verify\mutation-launcher.log" 2>&1
>> "artifacts\verify\mutation-launcher.log" echo [launcher] finished exit=%ERRORLEVEL% %DATE% %TIME%
