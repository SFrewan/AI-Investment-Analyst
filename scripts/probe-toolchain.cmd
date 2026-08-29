@echo off
REM ---------------------------------------------------------------------------
REM  Double-clickable toolchain probe. Reports what this machine can build with,
REM  so a frontend architecture is chosen from evidence rather than assumption.
REM  Writes artifacts\verify\toolchain.txt. Reads nothing, changes nothing.
REM ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"
set OUT=artifacts\verify\toolchain.txt

> "%OUT%" echo [probe] started %DATE% %TIME%

>> "%OUT%" echo.
>> "%OUT%" echo --- node ---
where node >> "%OUT%" 2>&1
node -v >> "%OUT%" 2>&1

>> "%OUT%" echo.
>> "%OUT%" echo --- npm ---
where npm >> "%OUT%" 2>&1
call npm -v >> "%OUT%" 2>&1

>> "%OUT%" echo.
>> "%OUT%" echo --- dotnet sdks ---
dotnet --list-sdks >> "%OUT%" 2>&1

>> "%OUT%" echo.
>> "%OUT%" echo --- dotnet workloads ---
dotnet workload list >> "%OUT%" 2>&1

>> "%OUT%" echo.
>> "%OUT%" echo --- solution projects ---
dotnet sln "AI-Investment-Analyst.sln" list >> "%OUT%" 2>&1

>> "%OUT%" echo.
>> "%OUT%" echo [probe] finished exit=%ERRORLEVEL% %DATE% %TIME%
> "artifacts\verify\PROBE-DONE.txt" echo exit=%ERRORLEVEL%
endlocal
