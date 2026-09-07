#requires -Version 5.1
<#
    FULL HISTORICAL IDENTITY RECOVERY.

    At most 214 FREE SEC requests, ceiling enforced by the transport, and skipped entirely once
    the report exists so a re-run cannot spend them again.

    The EODHD connector is never switched on, so no billable call is possible from this script.
    NO prices. NO splits. NO dividends. NO corporate actions. NO scoring. NO orders.
    The sealed manifest is not modified, gate 2's floor is not touched, and price acquisition
    is NOT started.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot

function Clear-Switches {
    Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_RECOVERY_CORRECTION -ErrorAction SilentlyContinue
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
Write-Host '  SEC ceiling: 1 free request, enforced by the transport.'
Write-Host '  This can take several minutes: one filing per unpriceable member.'

$code = 0
$recoveryReport = Join-Path $root 'artifacts\universe\identity-recovery-corrected.json'

if (Test-Path $recoveryReport) {
    Write-Host ''
    Write-Host '  Recovery already run and its report is on disk. Skipping: the authorised'
    Write-Host '  SEC calls are spent and this script will not spend them again.'
}
else {
    try {
        $env:AIINV_RECOVERY_CORRECTION = '1'

        & powershell -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
            -Filter 'FullyQualifiedName~RecoveryCorrectionTests' `
            -LogName 'recovery-correction.log' `
            -Label 'recovery correction, at most 1 free SEC request' | Out-Host

        $code = [int]$LASTEXITCODE
    }
    finally {
        Remove-Item Env:\AIINV_RECOVERY_CORRECTION -ErrorAction SilentlyContinue
    }
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecurityClassificationTests|FullyQualifiedName~RecoveryPartitionTests|FullyQualifiedName~SurvivorshipProbeSelectionTests|FullyQualifiedName~ObservationDeduplicationTests|FullyQualifiedName~UsEquitySessionTests|FullyQualifiedName~CoverageRuleTests|FullyQualifiedName~PriceAcquisitionPlanningTests|FullyQualifiedName~IdentityResolutionTests' `
        -LogName 'recovery-rules.log' `
        -Label 'recovery partition and every standing invariant' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    try {
        $env:AIINV_UNIVERSE_READINESS = '1'

        & powershell -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
            -Filter 'FullyQualifiedName~UniverseReadinessTests' `
            -LogName 'recovery-gates.log' `
            -Label 'the twelve gates, unchanged' | Out-Host

        $code = [int]$LASTEXITCODE
    }
    finally {
        Remove-Item Env:\AIINV_UNIVERSE_READINESS -ErrorAction SilentlyContinue
    }
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'recovery-full.log' -Label 'full Release suite' | Out-Host

    $code = [int]$LASTEXITCODE
}

Clear-Switches

Write-Host ''
Write-Host '=== Recovery outcome'
if (Test-Path $recoveryReport) {
    Get-Content $recoveryReport |
        Where-Object { $_ -match 'SEC calls attempted|Succeeded|Failed in transport|Not requested|EODHD|Recovered|No listed security|Unresolved|Transport failed|Baseline|After recovery|Sealed universe, for|remain' } |
        Select-Object -First 30 |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) }
}
else { Write-Host '  no recovery report written.' }

exit $code
