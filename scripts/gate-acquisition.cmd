@echo off
setlocal
rem READ-ONLY. The two admission-criteria decisions, and the deeper-history acquisition plan
rem checked without acquiring. Creates nothing, calls no provider, spends nothing.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  CRITERIA DECISIONS + ACQUISITION PLAN
echo   1. Release build, warnings are errors
echo   2. focused: search families, claims, register, gate
echo   3. acquisition plan, verified without acquiring
echo   NO provider calls. NO purchases. NO trades.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\acquisition-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\acquisition-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~SearchFamilyAndClaimTests|FullyQualifiedName~SampleRequirementTests|FullyQualifiedName~StrategyAdmissionTests|FullyQualifiedName~StrategyRegisterTests|FullyQualifiedName~ScoreStatisticsTests|FullyQualifiedName~ConditionalFrequencyTests" -LogName "acquisition-focused.log" -Label "focused: search families, claims, register, gate"
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" goto :done

set AIINV_ACQUISITION=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~PriceAcquisitionPlanTests" -LogName "acquisition-plan.log" -Label "acquisition plan, verified without acquiring"
set EXITCODE=%ERRORLEVEL%
set AIINV_ACQUISITION=

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Artifact: artifacts\verify\acquisition-plan.md
echo.
pause
endlocal
