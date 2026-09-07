@echo off
setlocal
rem BLOCK 3C - FOCUSED TESTS.
rem   The strategy admission gate and the EDGAR companyfacts normalizer.
rem   No EODHD request. No cycle. No execution. No paper trading.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  BLOCK 3C - FOCUSED TESTS
echo   Admission gate: declared event, resolved count, Brier bar,
echo   out-of-sample discipline, and the ranking property.
echo   SEC EDGAR companyfacts: normalization and persistence.
echo   No EODHD request. No cycle. No execution.
echo ===============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~StrategyAdmissionTests|FullyQualifiedName~AdmissionOrderingTests|FullyQualifiedName~AdmissionCompositionTests|FullyQualifiedName~CompanyFactsNormalizationTests" -LogName "gate-3c.log" -Label "block 3C: admission gate and companyfacts"
set EXITCODE=%ERRORLEVEL%

echo.
echo Exit code: %EXITCODE%
echo.
pause
endlocal
