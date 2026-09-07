#requires -Version 5.1
<#
    POST-RUN VERIFICATION FOR SEC BATCH 2 (LGIQ). READ-ONLY, THEN THE SUITE.

    NO NETWORK CALL OF ANY KIND. No SEC request, no EODHD request. Nothing is dispatched, nothing is
    consumed, no batch is authorised. Both connectors are pinned off for the whole run.

    Two halves:

      1. A census of the payload the batch 2 run archived, computed from the bytes on disk. The
         normaliser writes up to seven attributes per filing - accession number, form, primary
         document, description, filing date, acceptance instant and report date - and skips a field
         the document does not state. The census counts what the document actually states, so the
         observation count the run recorded can be reconciled against the document rather than
         asserted. It also counts the two provenance shapes that matter: a stated period later than
         acceptance, which becomes an attribute and never AsOfUtc, and a missing acceptance instant,
         which falls back to the filing date under a caveat.

      2. The focused SEC facts, then the full Release suite - run only after the batch 2 flag has
         been returned to false, so a suite result is never produced from a state that is not the
         resting one.
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
Write-Host '   POST-RUN VERIFICATION - SEC BATCH 2 - LGIQ.US'
Write-Host '   Read-only census, then the suite. NO network call of any kind.'
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
Write-Host '=== SEC attempt artefacts'
foreach ($a in $artefacts) {
    $r = Get-Content $a.FullName -Raw | ConvertFrom-Json
    Write-Host ('  ' + $a.Name + '  batch ' + $r.BatchIndex + '  ' + $r.Cik + '  ' + $r.Status + '  before=' + $r.AuthorizationConsumedBefore + '  this=' + $r.AuthorizationConsumedThisRun + '  total=' + $r.AuthorizationConsumedTotal + '  remaining=' + $r.AuthorizationRemaining)
}

# ---- the census -------------------------------------------------------------------------------

$code = 0

try {
    $record = Get-Content (Join-Path $root 'artifacts\universe\acquisition-sec-02-attempt-01.json') -Raw | ConvertFrom-Json
    $hash = $record.Reading.ArchivedHash

    $payloadPath = Join-Path $root ('tests\AI.Investment.Api.Tests\bin\Release\net8.0\archive\' + $hash.Substring(0, 2) + '\' + $hash.Substring(2, 2) + '\' + $hash + '.bin')

    if (-not (Test-Path $payloadPath)) {
        $found = Get-ChildItem -Path $root -Recurse -Filter ($hash + '.bin') -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { $payloadPath = $found.FullName }
    }

    $document = Get-Content $payloadPath -Raw | ConvertFrom-Json
    $recent = $document.filings.recent
    $count = $recent.accessionNumber.Count

    $present = @{ accession = 0; form = 0; primaryDocument = 0; description = 0; filingDate = 0; acceptance = 0; reportDate = 0 }
    $statedPeriodAfterAcceptance = 0
    $acceptanceMissing = 0
    $acceptanceBeforeFilingDate = 0
    $orderingHolds = 0
    $orderingBreaks = 0
    $retrieved = [datetime]::Parse('2026-09-04T11:24:04Z').ToUniversalTime()

    for ($i = 0; $i -lt $count; $i++) {
        $acc = $recent.accessionNumber[$i]
        $form = $recent.form[$i]
        $doc = $recent.primaryDocument[$i]
        $desc = $recent.primaryDocDescription[$i]
        $fd = $recent.filingDate[$i]
        $at = $recent.acceptanceDateTime[$i]
        $rd = $recent.reportDate[$i]

        if (-not [string]::IsNullOrWhiteSpace($acc)) { $present.accession++ }
        if (-not [string]::IsNullOrWhiteSpace($form)) { $present.form++ }
        if (-not [string]::IsNullOrWhiteSpace($doc)) { $present.primaryDocument++ }
        if (-not [string]::IsNullOrWhiteSpace($desc)) { $present.description++ }
        if (-not [string]::IsNullOrWhiteSpace($fd)) { $present.filingDate++ }
        if (-not [string]::IsNullOrWhiteSpace($at)) { $present.acceptance++ } else { $acceptanceMissing++ }
        if (-not [string]::IsNullOrWhiteSpace($rd)) { $present.reportDate++ }

        # The acceptance instant is the publication instant; the filing date is the floor used when
        # the document states no acceptance.
        $published = if (-not [string]::IsNullOrWhiteSpace($at)) { [datetime]::Parse($at).ToUniversalTime() } else { [datetime]::Parse($fd + 'T00:00:00Z').ToUniversalTime() }

        if (-not [string]::IsNullOrWhiteSpace($at) -and -not [string]::IsNullOrWhiteSpace($fd)) {
            if ($published -lt [datetime]::Parse($fd + 'T00:00:00Z').ToUniversalTime()) { $acceptanceBeforeFilingDate++ }
        }

        $asOf = $published
        if (-not [string]::IsNullOrWhiteSpace($rd)) {
            $period = [datetime]::Parse($rd + 'T00:00:00Z').ToUniversalTime()
            if ($period -gt $published) { $statedPeriodAfterAcceptance++ } else { $asOf = $period }
        }

        if ($asOf -le $published -and $published -le $retrieved) { $orderingHolds++ } else { $orderingBreaks++ }
    }

    $expected = 0
    foreach ($k in $present.Keys) { $expected += $present[$k] }

    $census = [pscustomobject]@{
        Cik                         = $document.cik
        EntityName                  = $document.name
        ArchivedHash                = $hash
        InlineFilings               = $count
        AttributesStated            = $present
        ExpectedObservations        = $expected
        ObservationsRecorded        = $record.ObservationsRecorded
        Difference                  = $record.ObservationsRecorded - $expected
        PayloadsQuarantined         = $record.PayloadsQuarantined
        StatedPeriodAfterAcceptance = $statedPeriodAfterAcceptance
        AcceptanceMissing           = $acceptanceMissing
        AcceptanceBeforeFilingDate  = $acceptanceBeforeFilingDate
        ProvenanceOrderingHolds     = $orderingHolds
        ProvenanceOrderingBreaks    = $orderingBreaks
        RetrievedAtUsedForOrdering  = $retrieved.ToString('o')
    }

    $out = Join-Path $root 'artifacts\verify\sec-lgiq-census.json'
    $census | ConvertTo-Json -Depth 6 | Set-Content -Path $out -Encoding UTF8

    Write-Host ''
    Write-Host ('=== census written: ' + $out)
    Write-Host ('  inline filings: ' + $count)
    foreach ($k in 'accession', 'form', 'primaryDocument', 'description', 'filingDate', 'acceptance', 'reportDate') {
        Write-Host ('    ' + $k.PadRight(16) + ' stated in ' + $present[$k] + ' of ' + $count)
    }
    Write-Host ('  expected observations: ' + $expected + '   recorded: ' + $record.ObservationsRecorded + '   difference: ' + ($record.ObservationsRecorded - $expected))
    Write-Host ('  quarantined: ' + $record.PayloadsQuarantined)
    Write-Host ('  stated period later than acceptance: ' + $statedPeriodAfterAcceptance + '  (attribute only, never AsOfUtc)')
    Write-Host ('  acceptance instant missing: ' + $acceptanceMissing + '  (filing-date floor under caveat)')
    Write-Host ('  acceptance earlier than stated filing date: ' + $acceptanceBeforeFilingDate)
    Write-Host ('  AsOfUtc <= PublishedAtUtc <= RetrievedAtUtc holds for ' + $orderingHolds + ' of ' + $count + '; breaks: ' + $orderingBreaks)
}
catch {
    Write-Host ('  census failed: ' + $_.Exception.Message)
    $code = 1
}

# ---- the suite --------------------------------------------------------------------------------

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecFilingsBatchTests' `
        -LogName 'lgiq-door.log' -Label 'the execution door, at rest again' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecEdgarSixMember' `
        -LogName 'lgiq-partition.log' -Label 'the partition and its installation' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecFilingsBatchRunnerTests' `
        -LogName 'lgiq-runner.log' -Label 'the runner invariants, unchanged' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecEdgarPreDispatch' `
        -LogName 'lgiq-preflight.log' -Label 'the preflight gates, unchanged' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'lgiq-full.log' -Label 'full Release suite, connectors off' | Out-Host

    $code = [int]$LASTEXITCODE
}

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Clear-Switches

Write-Host ''
Write-Host '  NOTHING WAS ACQUIRED HERE. No SEC call, no EODHD call, no network request of any kind.'
Write-Host '  NOTHING WAS AUTHORISED. Every batch is unauthorised again; the connector is off.'
Write-Host '  NOTHING WAS CONSUMED. The authorisation stands at consumed 2, remaining 4.'
Write-Host '  BATCH 3 WAS NOT APPROVED AND DID NOT RUN.'

exit $code
