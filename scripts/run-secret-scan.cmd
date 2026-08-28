@echo off
REM Double-clickable launcher for the tracked-file secret scan.
REM Writes artifacts\verify\secret-scan.log. It never prints a secret.
cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0secret-scan.ps1" > "artifacts\verify\secret-scan-launcher.log" 2>&1
