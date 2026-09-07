#requires -Version 5.1
<#
    FAILURE DIAGNOSIS. READ-ONLY.

    ZERO provider calls of any kind. Nothing is acquired and the acquisition is NOT resumed.
    Nothing is written to the database; run, observation and quarantine counts are asserted
    unchanged. No quarantined payload is reprocessed, reclassified or discarded. The sealed
    manifest is neither modified nor resealed, and the authorisation is neither amended nor
    increased.

    NOTE ON THE CONNECTOR SWITCH. Clearing the environment overrides below does NOT disable the
    EODHD connector: appsettings.Development.json sets Providers:Eodhd:Enabled to true, and the
    test host runs in the Development environment. This script therefore sets the switch to false
    explicitly rather than merely removing an override, so that the safety property is "the
    connector is off" and not "no test happened to dispatch".
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot

function Clear-Switches {
    Remove-Item Env:\AIINV_FAILURE_DIAGNOSIS -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_QUARANTINE_SURVEY -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_RESUME_READINESS -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_ACQUISITION_AUDIT -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_ACQUISITION_EXECUTE -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_POST_ACQUISITION -ErrorAction SilentlyContinue
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

# Off, stated, not merely un-overridden. The audit reads this value back and reports it.
$env:Providers__SecEdgar__Enabled = 'false'
$env:Providers__Eodhd__Enabled = 'false'

Write-Host ''
Write-Host '  Providers:Eodhd:Enabled and Providers:SecEdgar:Enabled are set to FALSE for this run.'
Write-Host '  READ-ONLY: nothing is acquired and nothing is written to the store.'

$code = 0

try {
    $env:AIINV_RESUME_READINESS = '1'

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~ResumeReadinessTests' `
        -LogName 'resume-readiness.log' `
        -Label 'resume readiness, read-only, connectors off' | Out-Host

    $code = [int]$LASTEXITCODE
}
finally {
    Remove-Item Env:\AIINV_FAILURE_DIAGNOSIS -ErrorAction SilentlyContinue
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AcquisitionAuthorizationLedgerTests|FullyQualifiedName~ProviderTransportDiagnosticTests|FullyQualifiedName~IngestionFailureDescriptionTests|FullyQualifiedName~CoverageEvaluationTests|FullyQualifiedName~PriceAcquisitionPlanningTests|FullyQualifiedName~IdentityResolutionTests|FullyQualifiedName~SecurityClassificationTests|FullyQualifiedName~ObservationDeduplicationTests|FullyQualifiedName~UsEquitySessionTests|FullyQualifiedName~CoverageRuleTests' `
        -LogName 'prepare-rules.log' `
        -Label 'every standing invariant, unchanged' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'prepare-full.log' -Label 'full Release suite' | Out-Host

    $code = [int]$LASTEXITCODE
}

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Clear-Switches

Write-Host ''
Write-Host '=== Diagnosis outcome'

$report = Join-Path $root 'artifacts\verify\resume-readiness.md'

if (Test-Path $report) {
    Get-Content $report |
        Where-Object { $_ -match 'Planned by|Suppressible|Outstanding|prices|splits|Authorised ceiling|Consumed by|Available now|Shortfall|shortfall is exactly|Batch|outside the authorised|Window on every|Subject kind|Runs before' } |
        Select-Object -First 45 |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) }
}
else { Write-Host '  no audit report written.' }

Write-Host ''
Write-Host '  NOTHING WAS ACQUIRED. No EODHD call, no SEC call, no price, no split, no dividend.'

exit $code
