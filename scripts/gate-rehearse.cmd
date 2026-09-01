@echo off
setlocal
rem BLOCK 3 - READ-ONLY DISCOVERY REHEARSAL.
rem   Reads the stored year through the production point-in-time path and counts what the
rem   existing screen would have found. Creates nothing. Writes nothing. Calls no provider.

cd /d "%~dp0.."
set AIINV_REHEARSE=1

echo.
echo ===============================================================
echo  BLOCK 3 - READ-ONLY DISCOVERY REHEARSAL
echo   20 instruments x 250 sessions, decision points after the
echo   60-session warm-up and before the 21-session horizon.
echo   NO opportunities. NO predictions. NO trades. NO API calls.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~DiscoveryRehearsalTests" -LogName "gate-rehearse.log" -Label "block 3: read-only discovery rehearsal"
set EXITCODE=%ERRORLEVEL%

set AIINV_REHEARSE=
echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
