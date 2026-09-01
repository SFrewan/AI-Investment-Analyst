@echo off
setlocal
rem BLOCK 3A/3B - FOCUSED TESTS.
rem   Episode deduplication and probability/validation-event alignment.
rem   No EODHD request. No cycle. No paper trading.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  BLOCK 3A/3B - FOCUSED TESTS
echo   Episode deduplication, event alignment, and the paths they
echo   touch: the screen, the discoverer and the work plan.
echo   No EODHD request. No cycle. No execution.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~PriceRecoveryEpisodeTests|FullyQualifiedName~EventAlignmentTests|FullyQualifiedName~PriceRecovery|FullyQualifiedName~EquityReviewWorkPlan|FullyQualifiedName~DiscoveryTests|FullyQualifiedName~CompositionTests" -LogName "gate-3ab.log" -Label "block 3A/3B: episodes and event alignment"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
