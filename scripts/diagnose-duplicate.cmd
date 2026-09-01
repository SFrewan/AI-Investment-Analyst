@echo off
setlocal
rem READ-ONLY diagnosis of the DuplicateSuppressed ingestion. SELECT only. Starts nothing.
cd /d "%~dp0.."
echo.
echo  READ-ONLY: why the post-fix ingestion was suppressed as a duplicate
echo  Transcript: artifacts\verify\diagnose-duplicate.txt
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0diagnose-duplicate.ps1"
echo.
echo Exit code: %ERRORLEVEL%
echo.
pause
endlocal
