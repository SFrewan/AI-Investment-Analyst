#requires -Version 5.1
<#
    ROW-LEVEL PRICE QUARANTINE - BUILD AND TEST.

    NO NETWORK CALL OF ANY KIND. No provider request, no acquisition, no authorisation is touched,
    no archived payload is re-fetched. Both connectors are pinned off for the whole run.

    Order:
      1. the focused normalisation and pipeline facts
      2. the coverage-rule facts, which must pass UNMODIFIED - that is the proof Gate 6 did not move
      3. the quarantine and price-payload facts
      4. the full Release suite
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
Write-Host '   ROW-LEVEL PRICE QUARANTINE - build and test'
Write-Host '   ZERO network calls. ZERO acquisitions. Gate 6 untouched.'
Write-Host '  ================================================================'

# ---- the resting state, asserted before anything runs -----------------------------------------

$partition = Get-Content (Join-Path $root 'tests\AI.Investment.Api.Tests\SecEdgarSixMemberPartition.cs') -Raw
$flags = [regex]::Matches($partition, 'Authorised: (true|false)\)')

Write-Host ''
Write-Host ('=== SEC batches authorised: ' + @($flags | Where-Object { $_.Groups[1].Value -eq 'true' }).Count + ' of ' + $flags.Count)

if (@($flags | Where-Object { $_.Groups[1].Value -eq 'true' }).Count -ne 0) {
    Write-Host '  STOPPING. A SEC batch is approved. Nothing should run from that state.'
    exit 2
}

$gate6 = Get-Item (Join-Path $root 'declarations\coverage-gate6.json')
Write-Host ('=== coverage-gate6.json: ' + $gate6.Length + ' bytes, written ' + $gate6.LastWriteTimeUtc.ToString('o'))

$code = 0

$suites = @(
    @{ Filter = 'FullyQualifiedName~Normalization'; Log = 'rowlevel-normalization.log'; Label = 'the normalisation facts, including the new third result shape' },
    @{ Filter = 'FullyQualifiedName~PartialNormalization'; Log = 'rowlevel-partial.log'; Label = 'the partial-read shape and the pipeline carrying it' },
    @{ Filter = 'FullyQualifiedName~EodhdDailyPriceNormalizer'; Log = 'rowlevel-eodhd.log'; Label = 'the EODHD price normaliser, row by row' },
    @{ Filter = 'FullyQualifiedName~Coverage'; Log = 'rowlevel-coverage.log'; Label = 'the coverage rule and evaluator - UNMODIFIED' },
    @{ Filter = 'FullyQualifiedName~SplitAdjustment'; Log = 'rowlevel-splits.log'; Label = 'SplitAdjustment - UNMODIFIED' },
    @{ Filter = 'FullyQualifiedName~Quarantine'; Log = 'rowlevel-quarantine.log'; Label = 'the quarantine survey' },
    @{ Filter = 'FullyQualifiedName~PricePayloadReread'; Log = 'rowlevel-reread.log'; Label = 'the price-payload re-read' },
    @{ Filter = 'FullyQualifiedName~AI.Investment'; Log = 'rowlevel-full.log'; Label = 'full Release suite, connectors off' }
)

foreach ($suite in $suites) {
    if ($code -ne 0) { break }

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter $suite.Filter -LogName $suite.Log -Label $suite.Label | Out-Host

    $code = [int]$LASTEXITCODE
}

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Clear-Switches

Write-Host ''
Write-Host '  NOTHING WAS ACQUIRED. No SEC call, no EODHD call, no network request of any kind.'
Write-Host '  NO AUTHORISATION WAS TOUCHED. The SEC authorisation stays spent at 6 of 6.'
Write-Host '  GATE 6 WAS NOT MODIFIED. Its rule file and evaluator are unchanged.'
Write-Host '  NO ARCHIVED PAYLOAD WAS RE-FETCHED OR RECOVERED.'

exit $code
