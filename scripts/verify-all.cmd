@echo off
setlocal
rem IDEMPOTENCY SCOPE FIX: build, test, then live end-to-end verification.
rem   Tests gate the live phase. If anything is red the API is never started
rem   and no EODHD request is made. The 4-hour watch cooldown is NOT changed.
cd /d "%~dp0.."
echo.
echo ===============================================================
echo  IDEMPOTENCY SCOPE FIX - BUILD, TEST, LIVE
echo.
echo   1 build Release        4 full suite
echo   2 focused ingestion    5 build API
echo   3 WriteGuardTests      6 live cycle + 7 verify
echo.
echo  A real EODHD request is made ONLY if every test is green.
echo  The API is stopped automatically.
echo  Transcript: artifacts\verify\full-verification-*.txt
echo ===============================================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-all.ps1"
echo.
echo Exit code: %ERRORLEVEL%
echo.
pause
endlocal
