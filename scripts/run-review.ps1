#requires -Version 5.1
<#
    POST-BATCH-1 PLANNING AND INTEGRITY REVIEW. READ-ONLY.

    ZERO provider calls of any kind. No EODHD call, no SEC call, no price, split or dividend
    acquisition. No acquisition switch is set at any point in this script. Batch 2 is NOT run and
    is NOT marked authorised.

    Nothing is written to the database: run, observation and quarantine counts are asserted
    unchanged. No quarantined payload is reprocessed, reclassified or discarded, and no parsing or
    normalisation behaviour is changed. The authorisation declaration, the sealed manifest and the
    identity table are all read and none is modified.

    NOTE ON THE CONNECTOR SWITCH. Clearing the environment overrides does NOT disable a connector:
    appsettings.Development.json sets Providers:Eodhd:Enabled to true and the test host runs in the
    Development environment. Both switches are therefore set to false EXPLICITLY, so the safety
    property is "the connector is off" and not "no test happened to dispatch".
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
Write-Host '  READ-ONLY: nothing is acquired, nothing is written, no batch is authorised.'

$code = 0

try {
    $env:AIINV_POST_BATCH_REVIEW = '1'

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~PostBatchReviewTests' `
        -LogName 'post-batch-review.log' `
        -Label 'post-batch-1 review, read-only, connectors off' | Out-Host

    $code = [int]$LASTEXITCODE
}
finally { Remove-Item Env:\AIINV_POST_BATCH_REVIEW -ErrorAction SilentlyContinue }

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AcquisitionAuthorizationLedgerTests|FullyQualifiedName~ProviderTransportDiagnosticTests|FullyQualifiedName~IngestionFailureDescriptionTests|FullyQualifiedName~CoverageEvaluationTests|FullyQualifiedName~PriceAcquisitionPlanningTests|FullyQualifiedName~IdentityResolutionTests|FullyQualifiedName~SecurityClassificationTests|FullyQualifiedName~ObservationDeduplicationTests|FullyQualifiedName~UsEquitySessionTests|FullyQualifiedName~CoverageRuleTests' `
        -LogName 'review-rules.log' `
        -Label 'every standing invariant, unchanged' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'review-full.log' -Label 'full Release suite, connectors off' | Out-Host

    $code = [int]$LASTEXITCODE
}

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Clear-Switches

Write-Host ''
Write-Host '=== Review outcome'

$report = Join-Path $root 'artifacts\verify\post-batch-review.md'

if (Test-Path $report) {
    Get-Content $report |
        Where-Object { $_ -match 'Planned by|Suppressible|Outstanding|Shortfall|Consumed|Remaining|Failed price|Gate 6|Gate 9|Gate 12|zero|Zero|non-zero|terminal|interior|Manifest bytes|SHA-256|Members|Acquisition-ready|non-equity|Public settable|New runs|New observations|Batch 1|Before batch' } |
        Select-Object -First 70 |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) }
}
else { Write-Host '  no review report written.' }

Write-Host ''
Write-Host '  NOTHING WAS ACQUIRED. No EODHD call, no SEC call, no price, no split, no dividend.'
Write-Host '  BATCH 2 WAS NOT RUN AND IS STILL MARKED UNAUTHORISED.'

exit $code
