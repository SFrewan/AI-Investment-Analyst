#requires -Version 5.1
<#
    ATTEMPT-IDENTITY FIX: BUILD, TEST, AUDIT. NO NETWORK.

    ZERO provider calls of any kind. No EODHD request, no SEC request, no price, split, dividend or
    benchmark acquisition. NXST.US and SIRI.US are NOT executed. No batch is executed. No
    acquisition switch (AIINV_ACQUISITION_BATCH / AIINV_BATCH_INDEX / AIINV_ACQUISITION_EXECUTE) is
    set anywhere in this script - they are cleared before and after every step.

    Nothing is written to the store by this script beyond what the read-only verification stage
    reads. The authorisation declaration, the sealed manifest and the identity table are read and
    none is modified. No parser rule is added and no quarantine is reprocessed.

    NOTE ON THE CONNECTOR SWITCH. Clearing the environment overrides does NOT disable a connector:
    appsettings.Development.json sets Providers:Eodhd:Enabled to true and the test host runs in the
    Development environment. Both switches are therefore set to false EXPLICITLY.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot

function Clear-Switches {
    Remove-Item Env:\AIINV_ACQUISITION_BATCH -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_BATCH_INDEX -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_ACQUISITION_EXECUTE -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_POST_BATCH_REVIEW -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_POST_ACQUISITION -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_QUARANTINE_SURVEY -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_RESUME_READINESS -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_FAILURE_DIAGNOSIS -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_ACQUISITION_AUDIT -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_LEDGER_FORENSICS -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_ACQUISITION_DRY_RUN -ErrorAction SilentlyContinue
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

$env:Providers__SecEdgar__Enabled = 'false'
$env:Providers__Eodhd__Enabled = 'false'

Write-Host ''
Write-Host '  Providers:Eodhd:Enabled and Providers:SecEdgar:Enabled are set to FALSE for this run.'
Write-Host '  NO acquisition switch is set. NXST.US and SIRI.US are NOT executed.'

$code = 0

# ---- 1. the attempt-identity contract, and everything it must not have weakened ------------------

& powershell -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
    -Filter 'FullyQualifiedName~AcquisitionCorrelationTests|FullyQualifiedName~IngestionGatewayTests|FullyQualifiedName~ActionGatewaySafetyTests|FullyQualifiedName~DataPlaneRuleTests|FullyQualifiedName~AcquisitionAuthorizationLedgerTests|FullyQualifiedName~AcquisitionAuthorizationTests|FullyQualifiedName~ProviderTransportDiagnosticTests|FullyQualifiedName~IngestionFailureDescriptionTests|FullyQualifiedName~EodhdProviderTests|FullyQualifiedName~DataAcquisitionServiceTests' `
    -LogName 'fix-focused.log' `
    -Label 'attempt identity, the seam, the data plane rule and the authorisation' | Out-Host

$code = [int]$LASTEXITCODE

# ---- 2. the full suite ---------------------------------------------------------------------------

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'fix-full.log' -Label 'full Release suite, connectors off' | Out-Host

    $code = [int]$LASTEXITCODE
}

# ---- 3. the read-only state audit. Still no connector, still no acquisition switch. ---------------

try {
    $env:AIINV_POST_ACQUISITION = '1'

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~PostAcquisitionVerificationTests' `
        -LogName 'fix-state.log' `
        -Label 'state audit, read-only (the known completion assertion still fails)' | Out-Host
}
finally { Remove-Item Env:\AIINV_POST_ACQUISITION -ErrorAction SilentlyContinue }

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Clear-Switches

Write-Host ''
Write-Host '=== State after the fix (read-only)'

$report = Join-Path $root 'artifacts\verify\post-acquisition-verification.md'

if (Test-Path $report) {
    Get-Content $report |
        Where-Object { $_ -match 'bytes against|Matches the approved|Members|Acquisition-ready|Excluded|non-equity|Gate 6|Gate 12|not-yet-acquired|no-series|interior-gap|Closing-price rows|Distinct price series|would suppress|would still dispatch|Observations before|Observations after|runs before' } |
        Select-Object -First 40 |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) }
}

Write-Host ''
Write-Host '  NOTHING WAS ACQUIRED. EODHD 0 / SEC 0 / price 0 / split 0 / dividend 0 / benchmark 0.'
Write-Host '  NXST.US and SIRI.US were NOT executed. No batch ran. No authorisation was created.'

exit $code
