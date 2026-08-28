@echo off
REM Double-clickable launcher that distils the newest Stryker report into a survivor list.
REM Writes artifacts\verify\survivors.txt and artifacts\verify\SURVIVORS-DONE.txt.
cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"
> "artifacts\verify\survivors-launcher.log" echo [launcher] survivors started %DATE% %TIME%
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0survivors.ps1" >> "artifacts\verify\survivors-launcher.log" 2>&1
>> "artifacts\verify\survivors-launcher.log" echo [launcher] finished exit=%ERRORLEVEL% %DATE% %TIME%
