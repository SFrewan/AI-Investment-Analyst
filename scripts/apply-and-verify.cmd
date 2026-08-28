@echo off
REM ---------------------------------------------------------------------------
REM  Phase 5: apply the delivered files, then run the build and the full suite.
REM
REM  Extracts artifacts\phase5-apply.zip over the repository (overwriting only
REM  the files it contains) and stamps every extracted file with the current
REM  time. The stamp is load-bearing: a zip carries its own timestamps, MSBuild
REM  decides what to recompile by comparing source times against the last
REM  output, and an extracted file that looks older than the assembly built
REM  from it is silently skipped - so the suite re-runs the previous build and
REM  reports the previous result.
REM
REM  Everything it writes lands in artifacts\, which .gitignore excludes.
REM ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"
> "artifacts\verify\apply.log" echo [apply] started %DATE% %TIME% in "%CD%"
del /q "artifacts\verify\DONE.txt" 2>nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; $zip=[IO.Compression.ZipFile]::OpenRead((Resolve-Path 'artifacts\phase5-apply.zip')); $names=@($zip.Entries | ForEach-Object { $_.FullName }); $zip.Dispose(); Expand-Archive -Path 'artifacts\phase5-apply.zip' -DestinationPath '.' -Force; $stamp=Get-Date; foreach ($n in $names) { $p=Join-Path (Get-Location) ($n -replace '/','\'); if (Test-Path -LiteralPath $p) { (Get-Item -LiteralPath $p).LastWriteTime=$stamp; Write-Output ('[apply] touched ' + $n) } }" >> "artifacts\verify\apply.log" 2>&1
>> "artifacts\verify\apply.log" echo [apply] extracted exit=%ERRORLEVEL%
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\verify.ps1" >> "artifacts\verify\apply.log" 2>&1
>> "artifacts\verify\apply.log" echo [apply] verify finished exit=%ERRORLEVEL% %DATE% %TIME%
endlocal
