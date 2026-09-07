#requires -Version 5.1
<#
    IDENTITY REPORT REGENERATION - ZERO PROVIDER CALLS.

    The EDGAR connector is deliberately NOT switched on. The connector reads its own section
    while the container is being BUILT, and appsettings pins Enabled:false; the environment
    variables that override that are the only thing which has ever enabled it here, and this
    script sets none of them. So no SEC request can leave the machine even if something asked
    for one. AIINV_UNIVERSE_RETRY is likewise not set, so the stage does not ask.

    No EODHD variable is set and no billable call is possible from here.
    No price acquisition, no corporate actions, no scoring, no declarations, no orders.

    The last authorised SEC request stays unspent: 366 of 367 used, 1 remaining.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot

# Cleared rather than assumed absent: a variable left over from the previous stage's shell would
# otherwise switch the connector back on behind this script's back.
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__ApplicationName -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__ContactEmail -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__MaxRequestsPerSecond -ErrorAction SilentlyContinue
Remove-Item Env:\AIINV_UNIVERSE_RETRY -ErrorAction SilentlyContinue

$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if (Test-Path -Path $localSettings) { . $localSettings }

# verify.local.ps1 supplies the integration database; it must not have supplied a connector switch.
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\AIINV_UNIVERSE_RETRY -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '  EDGAR connector NOT switched on. No provider call is possible from this run.'
Write-Host '  Retry of the unresolved CIK is off. The final SEC request stays unspent.'

$code = 0

try {
    $env:AIINV_UNIVERSE_READINESS = '1'

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~UniverseReadinessTests' `
        -LogName 'readiness-stage.log' `
        -Label 'identity report regeneration, zero provider calls' | Out-Host

    $code = [int]$LASTEXITCODE
}
finally {
    Remove-Item Env:\AIINV_UNIVERSE_READINESS -ErrorAction SilentlyContinue
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~IdentityResolutionTests' `
        -LogName 'readiness-focused.log' `
        -Label 'focused identity rules' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'readiness-full.log' -Label 'full Release suite' | Out-Host

    $code = [int]$LASTEXITCODE
}

Write-Host ''
Write-Host '=== Readiness outcome'

$report = Join-Path $root 'artifacts\verify\universe-identity-final.md'
if (Test-Path $report) {
    Get-Content $report |
        Where-Object { $_ -match 'SEC calls|Cumulative|EODHD|Retry of|READY|NOT READY|genuine conflicts|sec-confirmed|sec-conflict|sec-only|provisional|ambiguous|unmatched|PASS|FAIL|deferred' } |
        Select-Object -First 60 |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) }
}
else {
    Write-Host '  no report written - the stage did not reach its end.'
}

exit $code
