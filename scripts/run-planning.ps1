#requires -Version 5.1
<#
    PRICE ACQUISITION PLANNING - ZERO PROVIDER CALLS.

    No connector is switched on. The EDGAR and EODHD connectors read their own sections while the
    container is being BUILT and appsettings pins both to Enabled:false; the environment variables
    that override that are the only thing that has ever enabled them here, and this script clears
    them rather than setting them. No request can leave the machine.

    No price, split, dividend, corporate action, score, opportunity, strategy or order.
    The remaining SEC call stays unspent. No EODHD call is spent.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot

function Clear-Providers {
    Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__SecEdgar__ApplicationName -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__SecEdgar__ContactEmail -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__SecEdgar__MaxRequestsPerSecond -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_UNIVERSE_RETRY -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_UNIVERSE_READINESS -ErrorAction SilentlyContinue
}

Clear-Providers

$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if (Test-Path -Path $localSettings) { . $localSettings }

# Sourced last, so anything the local settings file switches on is switched back off.
Clear-Providers

Write-Host ''
Write-Host '  No connector switched on. No provider call is possible from this run.'
Write-Host '  Planning only: the plan is computed from files already on disk.'

$code = 0

& powershell -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
    -Filter 'FullyQualifiedName~PriceAcquisitionPlanningTests|FullyQualifiedName~UsEquitySessionTests' `
    -LogName 'planning-focused.log' `
    -Label 'planning invariants and the DST session resolver' | Out-Host

$code = [int]$LASTEXITCODE

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~IdentityResolutionTests' `
        -LogName 'planning-identity.log' `
        -Label 'identity rules' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'planning-full.log' -Label 'full Release suite' | Out-Host

    $code = [int]$LASTEXITCODE
}

Write-Host ''
Write-Host '=== Planning artifact'

$plan = Join-Path $root 'artifacts\verify\price-acquisition-plan.md'
if (Test-Path $plan) {
    Get-Content $plan |
        Where-Object { $_ -match 'Acquisition-ready|Not ready|Minimum billable|SEC budget|Dropout share|PLANNING COMPLETE' } |
        Select-Object -First 20 |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) }
}
else {
    Write-Host '  the plan artifact is missing.'
}

exit $code
