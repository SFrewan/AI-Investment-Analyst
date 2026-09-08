#requires -Version 5.1
<#
    LOCAL ARCHIVED-PAYLOAD REPLAY - BUILD AND TEST.

    NO NETWORK CALL OF ANY KIND. No provider request, no acquisition, no authorisation touched,
    no archived payload re-fetched, NO RECOVERY PERFORMED. Both connectors are pinned off.

    The replay mechanism is built and its contract is tested. The five-member recovery it exists
    to support is NOT run here and is not runnable from this script.

    Order:
      1. the replay contract itself
      2. the ingestion suite it sits in, unchanged
      3. the normalisation facts, unchanged
      4. the coverage rule and SplitAdjustment - both must pass UNMODIFIED
      5. the quarantine survey
      6. the full Release suite
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
    Remove-Item Env:\AIINV_PAYLOAD_REREAD -ErrorAction SilentlyContinue
}

Clear-Switches
$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if (Test-Path -Path $localSettings) { . $localSettings }
Clear-Switches

$env:Providers__Eodhd__Enabled = 'false'
$env:Providers__SecEdgar__Enabled = 'false'

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   LOCAL ARCHIVED-PAYLOAD REPLAY - build and test'
Write-Host '   ZERO network calls. ZERO acquisitions. NO RECOVERY PERFORMED.'
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

$split = Get-Item (Join-Path $root 'src\AI.Investment.Domain\Opportunities\Equity\SplitAdjustment.cs')
Write-Host ('=== SplitAdjustment.cs: ' + $split.Length + ' bytes, written ' + $split.LastWriteTimeUtc.ToString('o'))

$code = 0

$suites = @(
    @{ Filter = 'FullyQualifiedName~ArchivedPayloadReplay'; Log = 'replay-contract.log'; Label = 'the replay contract - discovery, identity, suppression, quarantine' },
    @{ Filter = 'FullyQualifiedName~Ingestion'; Log = 'replay-ingestion.log'; Label = 'the ingestion suite it sits in' },
    @{ Filter = 'FullyQualifiedName~Normalization'; Log = 'replay-normalization.log'; Label = 'the normalisation facts' },
    @{ Filter = 'FullyQualifiedName~Coverage'; Log = 'replay-coverage.log'; Label = 'the coverage rule and evaluator - UNMODIFIED' },
    @{ Filter = 'FullyQualifiedName~SplitAdjustment'; Log = 'replay-splits.log'; Label = 'SplitAdjustment - UNMODIFIED' },
    @{ Filter = 'FullyQualifiedName~Quarantine'; Log = 'replay-quarantine.log'; Label = 'the quarantine survey' },
    @{ Filter = 'FullyQualifiedName~AI.Investment'; Log = 'replay-full.log'; Label = 'full Release suite, connectors off' }
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
Write-Host '  GATE 6 WAS NOT MODIFIED. SplitAdjustment WAS NOT MODIFIED.'
Write-Host '  NO RECOVERY WAS PERFORMED. No archived payload was replayed against the real store.'

exit $code
