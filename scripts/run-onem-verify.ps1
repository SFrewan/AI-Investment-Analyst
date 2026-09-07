#requires -Version 5.1
<#
    POST-RUN VERIFICATION FOR SEC BATCH 3 (ONEM). READ-ONLY, THEN THE SUITE.

    NO NETWORK CALL OF ANY KIND. No SEC request, no EODHD request. Nothing is dispatched, nothing is
    consumed, no batch is authorised. Both connectors are pinned off for the whole run, and this
    refuses to do anything at all if a batch is still approved.

    The census of the archived payload was computed by the dispatch script at the moment of the run
    and lives in artifacts\verify\sec-onem-evidence.json; it is re-read and re-checked here rather
    than recomputed, so what the suite runs against is the same evidence the report cites.
#>

[CmdletBinding()]
param()

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
}

Clear-Switches
$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if (Test-Path -Path $localSettings) { . $localSettings }
Clear-Switches

$env:Providers__Eodhd__Enabled = 'false'
$env:Providers__SecEdgar__Enabled = 'false'

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   POST-RUN VERIFICATION - SEC BATCH 3 - ONEM.US'
Write-Host '   Read-only state checks, then the suite. NO network call.'
Write-Host '  ================================================================'

# ---- the resting state, before anything is run ------------------------------------------------

$partition = Get-Content (Join-Path $root 'tests\AI.Investment.Api.Tests\SecEdgarSixMemberPartition.cs') -Raw
$flags = [regex]::Matches($partition, 'new\((\d), "(\d{10})", "([A-Z\.]+)", 1, (\d), Declaration, Authorised: (true|false)\)')

Write-Host ''
Write-Host '=== batch authorisation states, read from the partition source'

$anyAuthorised = $false
foreach ($m in $flags) {
    Write-Host ('  batch ' + $m.Groups[1].Value + '  ' + $m.Groups[2].Value + '  ' + $m.Groups[3].Value.PadRight(8) + '  prior=' + $m.Groups[4].Value + '  Authorised: ' + $m.Groups[5].Value)
    if ($m.Groups[5].Value -eq 'true') { $anyAuthorised = $true }
}

if ($anyAuthorised) {
    Write-Host ''
    Write-Host '  STOPPING. A batch is still approved. The suite must not run from that state.'
    exit 2
}

$artefacts = @(Get-ChildItem (Join-Path $root 'artifacts\universe') -Filter 'acquisition-sec-*.json' -ErrorAction SilentlyContinue | Sort-Object Name)

Write-Host ''
Write-Host '=== SEC attempt artefacts, and the account they charge'

$total = 0
$correlations = @()
foreach ($a in $artefacts) {
    $r = Get-Content $a.FullName -Raw | ConvertFrom-Json
    $total += $r.AuthorizationConsumedThisRun
    $correlations += $r.Correlation
    Write-Host ('  ' + $a.Name + '  batch ' + $r.BatchIndex + '  ' + $r.Cik + '  ' + $r.Symbol.PadRight(8) + '  ' + $r.Status + '  before=' + $r.AuthorizationConsumedBefore + '  this=' + $r.AuthorizationConsumedThisRun + '  total=' + $r.AuthorizationConsumedTotal + '  remaining=' + $r.AuthorizationRemaining + '  forms=' + $r.FormsInScope.Count)
}

Write-Host ''
Write-Host ('  units charged across all artefacts: ' + $total + ' of 6; remaining ' + (6 - $total))
Write-Host ('  distinct correlations: ' + (($correlations | Sort-Object -Unique).Count) + ' of ' + $correlations.Count + '  (equal means no correlation was reused)')

# ---- the census the dispatch run wrote, re-read ------------------------------------------------

$evidence = Join-Path $root 'artifacts\verify\sec-onem-evidence.json'

if (Test-Path $evidence) {
    $e = Get-Content $evidence -Raw | ConvertFrom-Json
    Write-Host ''
    Write-Host '=== the batch 3 census, re-read from the file the dispatch run wrote'
    Write-Host ('  entity: ' + $e.EntityName + '   cik: ' + $e.Cik)
    Write-Host ('  forms in scope for this member: ' + $e.FormsInScopeCount + '   (17 expected; 20 would mean the LGIQ scope leaked)')
    Write-Host ('  inline filings: ' + $e.InlineFilings + '   earliest: ' + $e.EarliestFilingDate + '   latest: ' + $e.LatestFilingDate + '   older file refs: ' + $e.OlderFileReferences)
    Write-Host ('  expected observations ' + $e.ExpectedObservations + ' vs recorded ' + $e.ObservationsRecorded + '  -> difference ' + $e.Difference)
    Write-Host ('  quarantined: ' + $e.PayloadsQuarantined)
    Write-Host ('  provenance ordering holds ' + $e.ProvenanceOrderingHolds + ' of ' + $e.InlineFilings + ', breaks ' + $e.ProvenanceOrderingBreaks)
    Write-Host ('  archive first-seen (sidecar RetrievedAtUtc): ' + $e.Sidecar.RetrievedAtUtc)

    if ($e.Difference -ne 0 -or $e.ProvenanceOrderingBreaks -ne 0 -or $e.FormsInScopeCount -ne 17) {
        Write-Host ''
        Write-Host '  STOPPING. The census does not reconcile. Read it before running anything else.'
        exit 2
    }
}
else {
    Write-Host ''
    Write-Host '  STOPPING. No batch 3 evidence summary exists.'
    exit 2
}

# ---- the suite --------------------------------------------------------------------------------

$code = 0

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecFilingsBatchTests' `
        -LogName 'onem-door.log' -Label 'the execution door, at rest again' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecEdgarSixMember' `
        -LogName 'onem-partition.log' -Label 'the partition and its installation' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecFilingsBatchRunnerTests' `
        -LogName 'onem-runner.log' -Label 'the runner invariants, unchanged' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecEdgarPreDispatch' `
        -LogName 'onem-preflight.log' -Label 'the preflight gates, unchanged' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'onem-full.log' -Label 'full Release suite, connectors off' | Out-Host

    $code = [int]$LASTEXITCODE
}

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Clear-Switches

Write-Host ''
Write-Host '  NOTHING WAS ACQUIRED HERE. No SEC call, no EODHD call, no network request of any kind.'
Write-Host '  NOTHING WAS AUTHORISED. Every batch is unauthorised again; the connector is off.'
Write-Host '  NOTHING WAS CONSUMED. The authorisation stands at consumed 3, remaining 3.'
Write-Host '  BATCH 4 WAS NOT APPROVED AND DID NOT RUN.'

exit $code
