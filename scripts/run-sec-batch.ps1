#requires -Version 5.1
<#
    ONE BATCH OF THE SEC EDGAR FILING ACQUISITION.

    THIS SCRIPT IS THE EXECUTION DOOR. IT IS WIRED AND IT IS SHUT.

    Running it today dispatches nothing. Every batch in the partition is marked unauthorised, and
    the runner refuses on that at its first gate - before a single service is resolved, before the
    connector is consulted, and without consuming an authorisation unit. That refusal is the
    expected outcome of this script until a batch is deliberately approved.

    SCOPE, and nothing wider, once a batch IS approved:
      * sec-edgar ONLY. No prices, no splits, no dividends, no benchmark, no scoring.
      * ONE company - one CIK - the single member of the named batch.
      * ONE request. The submissions endpoint returns one whole document and takes no period,
        so the provider request carries NO window. The authorisation's scope window
        (2021-09-01..2026-08-31) is used for Covers() and is never sent to the provider.
      * At most ONE dispatch, and never past the ceiling of 6 in
        declarations/acquisition-sec-edgar-gate6-six-members-2021-09-to-2026-08.json - which is
        NOT amended here. What earlier runs spent is read from their artefacts and charged first.

    EODHD is switched OFF throughout. SEC is switched on for the batch step ONLY, so every other
    step is provably unable to reach any provider.

    FAIR ACCESS. EDGAR requires every request to identify its origin. This script refuses before
    anything else if AIINV_SEC_CONTACT is not set, and never prints the value.

    NOTHING RUNS THE NEXT BATCH. Batches 2 to 6 are defined in the partition and marked
    unauthorised; each requires its own approval.
#>

[CmdletBinding()]
param([Parameter(Mandatory = $true)][int]$Batch)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot

function Clear-Switches {
    Remove-Item Env:\AIINV_SEC_BATCH -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SEC_BATCH_INDEX -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SEC_BACKFILL -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SPLIT_BATCH -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SPLIT_BATCH_INDEX -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_ACQUISITION_BATCH -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_BATCH_INDEX -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_ACQUISITION_EXECUTE -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_ACQUISITION_DRY_RUN -ErrorAction SilentlyContinue
}

Clear-Switches
$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if (Test-Path -Path $localSettings) { . $localSettings }
Clear-Switches

if ($Batch -lt 1 -or $Batch -gt 6) {
    Write-Host ''
    Write-Host ('  Batch ' + [string]$Batch + ' is not in the partition, which has six one-member batches.')
    Write-Host '  There is no default batch: a run that does not say which batch it is must not pick one.'
    Write-Host ''
    exit 2
}

# ---- fair-access identity, before anything else ---------------------------

if ([string]::IsNullOrWhiteSpace($env:AIINV_SEC_CONTACT)) {
    Write-Host ''
    Write-Host '  STOPPING before any request. AIINV_SEC_CONTACT is not set.'
    Write-Host '  EDGAR fair access requires every request to name a contact, and this run'
    Write-Host '  will not invent one. Set it in scripts/verify.local.ps1 (git-ignored).'
    Write-Host ''
    exit 2
}

Write-Host ''
Write-Host '  contact address configured (value not printed).'

# ---- both connectors off by default ---------------------------------------

$env:Providers__SecEdgar__Enabled = 'false'
$env:Providers__Eodhd__Enabled = 'false'

Write-Host ''
Write-Host '  ================================================================'
Write-Host ('   SEC BATCH ' + [string]$Batch + ': AT MOST ONE EDGAR REQUEST, FOR ONE CIK.')
Write-Host '   sec-edgar ONLY. No prices, no splits, no dividends.'
Write-Host '   The provider request carries NO window; the authorisation scope'
Write-Host '   window 2021-09-01..2026-08-31 is used for Covers() and nothing else.'
Write-Host '   The ceiling of 6 is enforced, not amended.'
Write-Host ''
Write-Host '   IF THE BATCH IS NOT APPROVED THIS REFUSES AND SPENDS NOTHING.'
Write-Host '   That is the expected outcome until a batch is deliberately'
Write-Host '   marked Authorised in the partition.'
Write-Host '  ================================================================'
Write-Host ''

$code = 0

try {
    # The connector reads its own section while the container is being built, so the switch is set
    # here for the duration of one step and cleared again below. appsettings pins it to false.
    $env:Providers__SecEdgar__Enabled = 'true'
    $env:Providers__SecEdgar__ApplicationName = 'AI-Investment-Analyst'
    $env:Providers__SecEdgar__ContactEmail = $env:AIINV_SEC_CONTACT
    $env:Providers__SecEdgar__MaxRequestsPerSecond = '5'

    $env:AIINV_SEC_BATCH = '1'
    $env:AIINV_SEC_BATCH_INDEX = [string]$Batch

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecFilingsBatchTests' `
        -LogName ('acquisition-sec-' + [string]$Batch + '.log') `
        -Label ('SEC batch ' + [string]$Batch + ', at most one EDGAR filing request') | Out-Host

    $code = [int]$LASTEXITCODE
}
finally {
    Remove-Item Env:\AIINV_SEC_BATCH -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SEC_BATCH_INDEX -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__SecEdgar__ApplicationName -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__SecEdgar__ContactEmail -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__SecEdgar__MaxRequestsPerSecond -ErrorAction SilentlyContinue
    $env:Providers__SecEdgar__Enabled = 'false'
}

# ---- verification, with both connectors off again -------------------------

$env:Providers__SecEdgar__Enabled = 'false'
$env:Providers__Eodhd__Enabled = 'false'

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecFilingsBatchRunnerTests' `
        -LogName ('acquisition-sec-' + [string]$Batch + '-runner.log') `
        -Label 'the runner invariants, connectors off' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName ('acquisition-sec-' + [string]$Batch + '-full.log') `
        -Label 'full Release suite, connectors off' | Out-Host

    $code = [int]$LASTEXITCODE
}

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Clear-Switches

$artefact = Join-Path $root ('artifacts\universe\acquisition-sec-' + $Batch.ToString('00') + '-attempt-01.json')

Write-Host ''
Write-Host '=== SEC batch outcome'
Write-Host ('  attempt record: ' + $artefact)
Write-Host ''
Write-Host '  NOTHING ELSE WAS ACQUIRED. No price, no split, no dividend request was made.'
Write-Host '  NO NEXT BATCH RAN. Each of the remaining batches requires its own approval.'
Write-Host '  THE CEILING WAS NOT AMENDED. The declaration and its digest are unchanged.'

exit $code
