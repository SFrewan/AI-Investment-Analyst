#requires -Version 5.1
<#
    WIDENED SURVIVORSHIP PROBE.

    At most 10 FREE SEC requests, ceiling enforced by the transport, and skipped entirely once
    the report exists so a re-run cannot spend ten more.

    The EODHD connector is never switched on, so no billable call is possible from this script.
    NO prices. NO splits. NO dividends. NO corporate actions. NO scoring. NO orders.
    The sealed universe is not modified and gate 2's floor is not touched.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot

function Clear-Switches {
    Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SURVIVORSHIP_PROBE -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SURVIVORSHIP_PROBE_2 -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SEC_TICKER_PROBE -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SEC_TICKER_REFINEMENT -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_PRICE_DEDUPLICATION -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_PRICE_SESSION_REMEDIATION -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_UNIVERSE_RETRY -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_UNIVERSE_READINESS -ErrorAction SilentlyContinue
}

Clear-Switches
$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if (Test-Path -Path $localSettings) { . $localSettings }
Clear-Switches

if ([string]::IsNullOrWhiteSpace($env:AIINV_SEC_CONTACT)) {
    Write-Host '  STOPPING. AIINV_SEC_CONTACT is not set and EDGAR fair access requires a contact.'
    exit 2
}

Write-Host ''
Write-Host '  EODHD connector NOT switched on. No billable call is possible from this run.'
Write-Host '  SEC ceiling: 10 free requests, enforced by the transport.'

$code = 0
$probeReport = Join-Path $root 'artifacts\verify\survivorship-probe-extended-20.md'

if (Test-Path $probeReport) {
    Write-Host ''
    Write-Host '  Probe already run and its report is on disk. Skipping: the ten authorised'
    Write-Host '  SEC calls are spent and this script will not spend ten more.'
}
else {
    try {
        $env:AIINV_SURVIVORSHIP_PROBE_2 = '1'

        & powershell -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
            -Filter 'FullyQualifiedName~SurvivorshipProbeExtendedTests' `
            -LogName 'survivorship-probe-2.log' `
            -Label 'second probe round, at most 10 free SEC requests' | Out-Host

        $code = [int]$LASTEXITCODE
    }
    finally {
        Remove-Item Env:\AIINV_SURVIVORSHIP_PROBE_2 -ErrorAction SilentlyContinue
    }
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SurvivorshipProbeSelectionTests|FullyQualifiedName~ObservationDeduplicationTests|FullyQualifiedName~UsEquitySessionTests|FullyQualifiedName~CoverageRuleTests|FullyQualifiedName~PriceAcquisitionPlanningTests|FullyQualifiedName~IdentityResolutionTests' `
        -LogName 'probe-rules.log' `
        -Label 'selection rule, de-duplication, session, coverage, planning, identity' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'probe-full.log' -Label 'full Release suite' | Out-Host

    $code = [int]$LASTEXITCODE
}

Clear-Switches

Write-Host ''
Write-Host '=== Probe outcome'
if (Test-Path $probeReport) {
    Get-Content $probeReport |
        Where-Object { $_ -match 'SEC calls|Succeeded|Recovered|Proven absent|Inconclusive|Transport failure|recovery rate|Wilson|Met\.|Not met|^\*\*B|Not A' } |
        Select-Object -First 30 |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) }
}
else { Write-Host '  no probe report written.' }

exit $code
