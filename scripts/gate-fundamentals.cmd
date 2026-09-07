@echo off
setlocal
rem READ-ONLY. The declared/derived sample bar, and the fundamentals strategy scored against the
rem stored SEC filings. Creates nothing, writes no opportunity, calls no provider, spends nothing.

cd /d "%~dp0.."

echo.
echo ===============================================================
echo  FUNDAMENTALS VALIDATION + DERIVED SAMPLE BAR
echo   1. Release build, warnings are errors
echo   2. focused: the admission bar and the fundamentals rule
echo   3. read-only fundamentals validation over the stored filings
echo   NO opportunities. NO predictions. NO trades. NO API calls.
echo ===============================================================
echo.

echo --- Release build (warnings are errors)
dotnet build "%~dp0..\AI-Investment-Analyst.sln" -c Release --nologo > "%~dp0..\artifacts\verify\fundamentals-build.log" 2>&1
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" (
  echo   FAIL  Release build
  findstr /C:"error " "%~dp0..\artifacts\verify\fundamentals-build.log"
  goto :done
)
echo   PASS  Release build
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~SampleRequirementTests|FullyQualifiedName~FundamentalDirectionTests|FullyQualifiedName~StrategyAdmissionTests|FullyQualifiedName~AdmissionOrderingTests" -LogName "fundamentals-focused.log" -Label "focused: the admission bar and the fundamentals rule"
set EXITCODE=%ERRORLEVEL%
if not "%EXITCODE%"=="0" goto :done

set AIINV_FUNDAMENTALS=1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gate-tests.ps1" -Filter "FullyQualifiedName~FundamentalsRehearsalTests" -LogName "fundamentals-rehearse.log" -Label "read-only fundamentals validation"
set EXITCODE=%ERRORLEVEL%
set AIINV_FUNDAMENTALS=

:done
echo.
echo Exit code: %EXITCODE%
echo.
echo Artifact: artifacts\verify\fundamentals.md
echo.
pause
endlocal
