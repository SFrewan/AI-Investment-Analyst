@echo off
setlocal
rem READ-ONLY. Five pre-registered strategies scored against the stored SEC filings and the stored
rem price year. Creates nothing, writes no opportunity, calls no provider, spends nothing.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  FIVE PRE-REGISTERED STRATEGIES
echo   1. Release build, warnings are errors
echo   2. focused: research engine, register, statistics, gate
echo   3. read-only research rehearsal
echo   NO opportunities. NO predictions. NO trades. NO API calls.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\research-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\research-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~ConditionalFrequencyTests|FullyQualifiedName~StrategyRegisterTests|FullyQualifiedName~ScoreStatisticsTests|FullyQualifiedName~SampleRequirementTests|FullyQualifiedName~StrategyAdmissionTests" -LogName "research-focused.log" -Label "focused: research engine, register, statistics, gate"
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" goto :done

set AIINV_RESEARCH=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~ResearchRehearsalTests" -LogName "research-rehearse.log" -Label "read-only research rehearsal"
set EXITCODE=%ERRORLEVEL%
set AIINV_RESEARCH=

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Artifact: artifacts\verify\research.md
echo.
pause
endlocal
