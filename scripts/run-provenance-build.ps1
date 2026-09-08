#requires -Version 5.1
<#
    PER-RUN REQUEST PROVENANCE - BUILD ONLY.

    Builds the solution in Release. NO tests, NO migration, NO database contact, NO provider call.
    This exists to surface compile errors before anything is applied to a database.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'AI-Investment-Analyst.sln'
$log = Join-Path $root 'artifacts\verify\provenance-build.log'

$null = New-Item -ItemType Directory -Force -Path (Join-Path $root 'artifacts\verify')

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   PER-RUN REQUEST PROVENANCE - BUILD ONLY (Release)'
Write-Host '   No tests. No migration. No database. No provider.'
Write-Host '  ================================================================'
Write-Host ''

$env:Providers__Eodhd__Enabled = 'false'
$env:Providers__SecEdgar__Enabled = 'false'

Write-Host ('  dotnet --version : ' + (& dotnet --version))
Write-Host ''
Write-Host '--- building the solution (Release)'

& dotnet build $sln -c Release --nologo 2>&1 | Tee-Object -FilePath $log | Out-Null
$code = $LASTEXITCODE

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue

if ($code -ne 0) {
    Write-Host '  FAIL  the solution did not build.'
    Write-Host ''

    $shown = 0
    foreach ($line in (Get-Content -Path $log)) {
        if ($line -match 'error [A-Z]+[0-9]+' -and $shown -lt 30) {
            Write-Host ('  ' + $line.Trim())
            $shown++
        }
    }

    Write-Host ''
    Write-Host ('  Full build log: ' + $log)
    exit 6
}

Write-Host '  PASS  built.'
Write-Host ''

foreach ($line in (Get-Content -Path $log -Tail 20)) {
    if ($line -match 'Warning|Error|Build succeeded') { Write-Host ('  ' + $line.Trim()) }
}

exit 0
