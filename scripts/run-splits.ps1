#requires -Version 5.1
<#
    ONE BATCH OF THE RESUMED PRICE ACQUISITION.

    This script SPENDS PROVIDER REQUESTS - at most 70, and only for the batch named on the
    command line. There is no default batch: run it without -Batch and it refuses.

    SCOPE, and nothing wider:
      * eodhd-splits ONLY. No prices, no dividends, no SEC. no benchmark, no scoring.
      * The 70 symbols of the named batch, all of them inside the authorised 355.
      * Window 2021-09-01..2026-08-31, the authorised one and no other.
      * At most 70 dispatches, and never past the 704 ceiling in
        declarations/acquisition-eodhd-sample400.json - which is NOT amended here. What the
        interrupted run already spent is read from its outcome artefact and charged first.

    SEC is switched OFF throughout. EODHD is switched ON for the batch step ONLY, and OFF again
    for verification, the survey and the full suite - so every other step is provably unable to
    reach a provider.

    NOTE ON THE CONNECTOR SWITCH. Clearing the environment overrides does NOT disable a connector:
    appsettings.Development.json sets Providers:Eodhd:Enabled to true and the test host runs in the
    Development environment. Both switches are therefore set explicitly rather than removed.

    NOTHING RUNS THE NEXT BATCH. Batch 2 is defined in the runner and marked unauthorised; it
    requires its own approval.
#>

[CmdletBinding()]
param([Parameter(Mandatory = $true)][int]$Batch)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot

function Clear-Switches {
    Remove-Item Env:\AIINV_SPLIT_BATCH -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SPLIT_BATCH_INDEX -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_ACQUISITION_EXECUTE -ErrorAction SilentlyContinue
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

# Off for the whole script, stated rather than assumed.
$env:Providers__SecEdgar__Enabled = 'false'
$env:Providers__Eodhd__Enabled = 'false'

$code = 0

# ---- 1. the authorisation and every standing invariant, with both connectors off ------------------

& powershell -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
    -Filter 'FullyQualifiedName~AcquisitionAuthorizationLedgerTests|FullyQualifiedName~AcquisitionSplitRunnerReadinessTests|FullyQualifiedName~AcquisitionCorrelationTests|FullyQualifiedName~ProviderTransportDiagnosticTests|FullyQualifiedName~IngestionFailureDescriptionTests|FullyQualifiedName~CoverageEvaluationTests|FullyQualifiedName~IdentityResolutionTests|FullyQualifiedName~SecurityClassificationTests|FullyQualifiedName~ObservationDeduplicationTests|FullyQualifiedName~DataPlaneRuleTests' `
    -LogName 'splits-rules.log' `
    -Label 'the successor authorisation, the split runner readiness and every standing invariant' | Out-Host

$code = [int]$LASTEXITCODE

# ---- 2. the batch itself. EODHD on, and only here. -----------------------------------------------

if ($code -ne 0) {
    Write-Host ''
    Write-Host '  STOPPING before the batch: an invariant failed. Nothing was spent.'
}
else {
    if ([string]::IsNullOrWhiteSpace($env:Providers__Eodhd__ApiKey)) {
        Write-Host ''
        Write-Host '  NOTE: no EODHD key in the process environment. The host also reads the'
        Write-Host '  user-secrets store and the per-user environment, which is where it has been.'
    }

    Write-Host ''
    Write-Host '  ================================================================'
    Write-Host ("   SPLIT BATCH " + $Batch + ": SPENDING AT MOST ONE EODHD REQUEST PER SYMBOL")
    Write-Host '   IN THE APPROVED BATCH, against a hard cap of 70.'
    Write-Host '   eodhd-splits ONLY. No prices, no dividends, no SEC.'
    Write-Host '   Window 2021-09-01..2026-08-31. The active ceiling is enforced, not amended.'
    Write-Host '   Paced at roughly one a second against a declared 60 a minute,'
    Write-Host '   so expect this step to take 2-4 minutes. Do not interrupt it:'
    Write-Host '   the record of what was spent is written when it finishes.'
    Write-Host '  ================================================================'
    Write-Host ''

    try {
        $env:Providers__Eodhd__Enabled = 'true'
        $env:AIINV_SPLIT_BATCH = '1'
        $env:AIINV_SPLIT_BATCH_INDEX = [string]$Batch

        & powershell -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
            -Filter 'FullyQualifiedName~AcquisitionSplitBatchTests' `
            -LogName ('acquisition-splits-' + $Batch + '.log') `
            -Label ('batch ' + $Batch + ', at most 70 EODHD split requests') | Out-Host

        $code = [int]$LASTEXITCODE
    }
    finally {
        Remove-Item Env:\AIINV_SPLIT_BATCH -ErrorAction SilentlyContinue
        Remove-Item Env:\AIINV_SPLIT_BATCH_INDEX -ErrorAction SilentlyContinue
        $env:Providers__Eodhd__Enabled = 'false'
    }
}

# ---- 3. verification and the quarantine survey, with EODHD off again ------------------------------

$env:Providers__Eodhd__Enabled = 'false'

try {
    $env:AIINV_POST_ACQUISITION = '1'

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~PostAcquisitionVerificationTests' `
        -LogName 'splits-post-acquisition.log' `
        -Label 'post-acquisition verification, read-only, connectors off' | Out-Host

    if ($code -eq 0) { $code = [int]$LASTEXITCODE }
}
finally { Remove-Item Env:\AIINV_POST_ACQUISITION -ErrorAction SilentlyContinue }

try {
    $env:AIINV_QUARANTINE_SURVEY = '1'

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~QuarantineSurveyTests' `
        -LogName 'splits-quarantine-survey.log' `
        -Label 'quarantine survey, read-only, nothing reprocessed' | Out-Host

    if ($code -eq 0) { $code = [int]$LASTEXITCODE }
}
finally { Remove-Item Env:\AIINV_QUARANTINE_SURVEY -ErrorAction SilentlyContinue }

# ---- 4. the full suite -----------------------------------------------------------------------------

$env:Providers__Eodhd__Enabled = 'false'

& powershell -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
    -Filter 'FullyQualifiedName~AI.Investment' `
    -LogName 'splits-full.log' -Label 'full Release suite, connectors off' | Out-Host

if ($code -eq 0) { $code = [int]$LASTEXITCODE }

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Clear-Switches

Write-Host ''
Write-Host '=== Batch outcome'

$label = '{0:00}' -f $Batch

$files = @(
    (Join-Path $root ('artifacts\verify\acquisition-splits-' + $label + '.md')),
    (Join-Path $root 'artifacts\verify\post-acquisition-verification.md'),
    (Join-Path $root 'artifacts\verify\quarantine-survey.md')
)

foreach ($file in $files) {
    if (Test-Path $file) {
        Write-Host ''
        Write-Host ("--- " + (Split-Path -Leaf $file))
        Get-Content $file |
            Where-Object { $_ -match 'Outstanding|In this batch|Suppressed|Dispatched|succeeded|failed|refused|Batch cap|Authorisation|Observations|runs before|runs after|quarantin|stopped|socket:|http:|status:|Gate |Token|terminal|all bad|Readable rows|By source|Payloads' } |
            Select-Object -First 55 |
            ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) }
    }
}

Write-Host ''
Write-Host ('  NO FURTHER SPLIT BATCH RAN. Only batch ' + $Batch + ' was authorised; every other batch in')
Write-Host '  the runner is marked unauthorised and requires its own approval.'
Write-Host '  No prices, no dividends and no SEC call were made.'

exit $code
