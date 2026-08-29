@echo off
REM Double-clickable wrapper for the read-only EODHD credential probe.
REM It reports booleans and counts only, never the credential, and makes no
REM network request. See probe-eodhd-credential.ps1.
setlocal
cd /d "%~dp0.."
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\probe-eodhd-credential.ps1"
> "artifacts\verify\EODHD-PROBE-DONE.txt" echo exit=%ERRORLEVEL%
endlocal
