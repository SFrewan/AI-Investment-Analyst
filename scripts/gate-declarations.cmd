@echo off
setlocal
rem READ-ONLY. The committed declaration register, the shared score statistics, and both rehearsals
rem re-scored against declarations that are no longer back-dated. Creates nothing, calls no provider.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  DECLARATION REGISTER + BOTH REHEARSALS
echo   1. Release build, warnings are errors
echo   2. focused: register, statistics, admission bar, rules
echo   3. read-only price rehearsal
echo   4. read-only fundamentals validation (pooled + revenue)
echo   NO opportunities. NO predictions. NO trades. NO API calls.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\declarations-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\declarations-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~StrategyRegisterTests|FullyQualifiedName~ScoreStatisticsTests|FullyQualifiedName~SampleRequirementTests|FullyQualifiedName~FundamentalDirectionTests|FullyQualifiedName~StrategyAdmissionTests|FullyQualifiedName~AdmissionOrderingTests" -LogName "declarations-focused.log" -Label "focused: register, statistics, admission bar, rules"
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" goto :done

set AIINV_REHEARSE=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~DiscoveryRehearsalTests" -LogName "declarations-price.log" -Label "read-only price rehearsal"
set EXITCODE=%ERRORLEVEL%
set AIINV_REHEARSE=
if not "%EXITCODE%"=="0" goto :done

set AIINV_FUNDAMENTALS=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~FundamentalsRehearsalTests" -LogName "declarations-fundamentals.log" -Label "read-only fundamentals validation"
set EXITCODE=%ERRORLEVEL%
set AIINV_FUNDAMENTALS=

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Artifacts: artifacts\verify\rehearsal.md and artifacts\verify\fundamentals.md
echo.
pause
endlocal
