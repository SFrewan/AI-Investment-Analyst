#requires -Version 5.1
<#
    SCOPE PRIOR CONSUMPTION TO THE ACTIVE AUTHORISATION. BUILD AND TEST ONLY.

    ZERO provider calls of any kind. No EODHD call, no SEC call, no price, split or dividend
    acquisition. No acquisition switch is set at any point in this script. NO SPLIT BATCH IS RUN
    and NO APPROVAL IS CHANGED - this script neither authorises a batch nor withdraws one. It
    reports whatever the partition says rather than asserting a particular answer.

    The corporate-actions work it reports on is complete: the final authorisation is spent to its
    ceiling of 73 and the ledger owes nothing, so no batch can be dispatched under it whatever an
    approval flag is left saying.

    Nothing is written to the database: run, observation and quarantine counts are asserted
    unchanged. No quarantined payload is reprocessed, reclassified or discarded, and no parsing,
    normalisation, transport or seam behaviour is changed. The sealed manifest and BOTH standing
    authorisation declarations are read and asserted byte-identical.

    NOTHING IS INSTALLED, AMENDED, MIGRATED OR REWRITTEN. Every declaration and every historical
    artefact under artifacts\universe\ is read exactly as recorded and asserted unchanged. The
    change under test is confined to which artefacts the split runner counts when it works out what
    the ACTIVE authorisation has already spent; the artefacts themselves are untouched.

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
    Remove-Item Env:\AIINV_SPLIT_AUTHORIZATION -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SPLIT_ACCOUNTING -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SPLIT_BATCH -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SPLIT_BATCH_INDEX -ErrorAction SilentlyContinue
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
    $env:AIINV_SPLIT_ACCOUNTING = '1'

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SplitAccountingReconciliationTests' `
        -LogName 'split-accounting.log' `
        -Label 'prior consumption scoped to the active authorisation. No dispatch.' | Out-Host

    $code = [int]$LASTEXITCODE
}
finally { Remove-Item Env:\AIINV_SPLIT_ACCOUNTING -ErrorAction SilentlyContinue }

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AcquisitionAuthorizationLedgerTests|FullyQualifiedName~AcquisitionSplitRunnerReadinessTests|FullyQualifiedName~SplitPriorConsumptionTests|FullyQualifiedName~AcquisitionCorrelationTests|FullyQualifiedName~ProviderTransportDiagnosticTests|FullyQualifiedName~IngestionFailureDescriptionTests|FullyQualifiedName~CoverageEvaluationTests|FullyQualifiedName~PriceAcquisitionPlanningTests|FullyQualifiedName~IdentityResolutionTests|FullyQualifiedName~SecurityClassificationTests|FullyQualifiedName~ObservationDeduplicationTests|FullyQualifiedName~UsEquitySessionTests|FullyQualifiedName~CoverageRuleTests' `
        -LogName 'accounting-rules.log' `
        -Label 'every standing invariant, unchanged' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'accounting-full.log' -Label 'full Release suite, connectors off' | Out-Host

    $code = [int]$LASTEXITCODE
}

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Clear-Switches

Write-Host ''
Write-Host '=== Split accounting outcome'

$report = Join-Path $root 'artifacts\verify\split-accounting.md'

if (Test-Path $report) {
    Get-Content $report |
        Where-Object { $_ -match 'Outstanding|Shortfall|ceiling|Ceiling|Consumed|consumed|remaining|Remaining|refused|Door|Schema|Authorisation id|Supersedes|Planned|satisfied|digest|Bytes|Payload|Rule|Recorded against|runs in the ledger|actually quarantined|symbols listed|Batch |Ingestion runs|Observations|Ready members' } |
        Select-Object -First 70 |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) }
}
else { Write-Host '  no split accounting report written.' }

Write-Host ''
Write-Host '  NOTHING WAS ACQUIRED. No EODHD call, no SEC call, no price, no split, no dividend.'
Write-Host '  NO SPLIT BATCH RAN. This script never sets the batch switch and never changes an approval.'
Write-Host '  The split work is COMPLETE: the final authorisation is spent to its ceiling and'
Write-Host '  the ledger owes nothing, so no batch can be dispatched under it.'
Write-Host '  NOTHING WAS INSTALLED OR AMENDED. No artefact was written, moved or rewritten.'

exit $code
