@echo off
setlocal
rem Prints the exact environment variables for a 5-minute test boundary.
rem READ ONLY: computes a timestamp and prints it. Changes nothing, starts nothing.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$b=(Get-Date).ToUniversalTime().AddMinutes(5); Write-Host ''; Write-Host '  Boundary (UTC)   : ' -NoNewline; Write-Host $b.ToString('yyyy-MM-ddTHH:mm:ssZ') -ForegroundColor Green; Write-Host '  Boundary (local) : ' -NoNewline; Write-Host $b.ToLocalTime().ToString('HH:mm:ss'); Write-Host ''; Write-Host '  cmd:' -ForegroundColor Yellow; Write-Host ('    set DevelopmentSchedule__TargetIdentifier=AAPL.US'); Write-Host ('    set DevelopmentSchedule__BoundaryUtc=' + $b.ToString('yyyy-MM-ddTHH:mm:ssZ')); Write-Host ('    set OperationsHost__RunCycles=true'); Write-Host ''; Write-Host '  PowerShell:' -ForegroundColor Yellow; Write-Host ('    $env:DevelopmentSchedule__TargetIdentifier = ''AAPL.US'''); Write-Host ('    $env:DevelopmentSchedule__BoundaryUtc = ''' + $b.ToString('yyyy-MM-ddTHH:mm:ssZ') + ''''); Write-Host ('    $env:OperationsHost__RunCycles = ''true'''); Write-Host ''"
pause
endlocal
