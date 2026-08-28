@echo off
REM ---------------------------------------------------------------------------
REM  Apply the delivered files and compile only - the fast inner loop.
REM
REM  Same extraction and timestamp handling as apply-and-verify.cmd, then
REM  verify.ps1 -SkipTests. A compile error comes back in about twenty seconds
REM  instead of a minute, which is the difference between iterating and waiting
REM  when the only way to start a process on this machine is to click a file.
REM
REM  The timestamp stamp is load-bearing: a zip carries its own times, MSBuild
REM  decides what to recompile by comparing source times against the last output,
REM  and an extracted file that looks older than the assembly built from it is
REM  silently skipped - so the build reports the previous result.
REM
REM  Everything it writes lands in artifacts\, which .gitignore excludes.
REM ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0.."
if not exist "artifacts\verify" mkdir "artifacts\verify"
> "artifacts\verify\apply.log" echo [apply] build-only started %DATE% %TIME% in "%CD%"
del /q "artifacts\verify\DONE.txt" 2>nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; $zip=[IO.Compression.ZipFile]::OpenRead((Resolve-Path 'artifacts\phase5-apply.zip')); $names=@($zip.Entries | ForEach-Object { $_.FullName }); $zip.Dispose(); Expand-Archive -Path 'artifacts\phase5-apply.zip' -DestinationPath '.' -Force; $stamp=Get-Date; foreach ($n in $names) { $p=Join-Path (Get-Location) ($n -replace '/','\'); if (Test-Path -LiteralPath $p) { (Get-Item -LiteralPath $p).LastWriteTime=$stamp; Write-Output ('[apply] touched ' + $n) } }" >> "artifacts\verify\apply.log" 2>&1
>> "artifacts\verify\apply.log" echo [apply] extracted exit=%ERRORLEVEL%
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\verify.ps1" -SkipTests >> "artifacts\verify\apply.log" 2>&1
>> "artifacts\verify\apply.log" echo [apply] build finished exit=%ERRORLEVEL% %DATE% %TIME%
endlocal
