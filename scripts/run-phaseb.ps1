#requires -Version 5.1
<#
    PHASE B - SEC EDGAR RUNNER. IMPLEMENTATION AND TESTING ONLY. ZERO DISPATCH.

    ZERO provider calls of any kind. No SEC call, no EODHD call, no network request whatsoever.
    The SEC connector is NOT enabled: both provider switches are forced false for this run, and the
    only connector these tests construct is a stub whose FetchAsync throws.

    NOTHING CALLS THE RUNNER EXCEPT TESTS WITH FAKES. There is no environment-variable door wired to
    the real container in this phase; every RunAsync in the repository is driven by a fake
    IDataAcquisition, a fake ledger and an in-memory archive.

    NO AUTHORIZATION IS CONSUMED. Every authorisation the tests exercise is synthetic, written to the
    temp directory from a digest computed with the production helper, and deleted afterwards. The
    installed SEC authorisation is loaded read-only and asserted at consumed 0 / remaining 6.

    NO BATCH IS AUTHORISED. Gate 6, the sealed manifest, the universe, the EODHD declarations, the
    price and split data, the ledger and the database are all untouched. One report is written:
    artifacts\verify\sec-edgar-runner-phase-b-report.md
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
    Remove-Item Env:\AIINV_SEC_BACKFILL -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SEC_BATCH -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SEC_BATCH_INDEX -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_POST_ACQUISITION -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_QUARANTINE_SURVEY -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_PAYLOAD_REREAD -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_MOVE_SCREEN -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_MOVE_CHARACTERISATION -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_COINCIDENCE_CHECK -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_RESUME_READINESS -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_FAILURE_DIAGNOSIS -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_ACQUISITION_AUDIT -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_LEDGER_FORENSICS -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_ACQUISITION_DRY_RUN -ErrorAction SilentlyContinue
}

Clear-Switches
$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if (Test-Path -Path $localSettings) { . $localSettings }
Clear-Switches

$env:Providers__SecEdgar__Enabled = 'false'
$env:Providers__Eodhd__Enabled = 'false'

Write-Host ''
Write-Host '  Providers:Eodhd:Enabled and Providers:SecEdgar:Enabled are set to FALSE for this run.'
Write-Host '  IMPLEMENTATION AND TESTING ONLY: nothing is dispatched, nothing is consumed.'

$code = 0

try {

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecFilingsBatchRunnerTests' `
        -LogName 'phaseb-runner.log' `
        -Label 'the SEC runner: every deterministic refusal before the charge, one charge before the dispatch' | Out-Host

    $code = [int]$LASTEXITCODE
}
finally { }

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecEdgarPreDispatchConsumptionTests' `
        -LogName 'phaseb-preflight.log' `
        -Label 'the Phase A preflight and normaliser gates, unchanged' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecEdgarFilingsNormalizerTests' `
        -LogName 'phaseb-normalizer.log' `
        -Label 'the regulatory-filings normaliser, unchanged' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'phaseb-full.log' -Label 'full Release suite, connectors off' | Out-Host

    $code = [int]$LASTEXITCODE
}

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Clear-Switches

Write-Host ''
Write-Host '=== Phase B outcome'

$report = Join-Path $root 'artifacts\verify\sec-edgar-runner-phase-b-report.md'

if (Test-Path $report) {
    Get-Content $report |
        Where-Object { $_ -match '^\| `|^\| \*\*|consumed|Remaining|dispatch|network' } |
        Select-Object -First 60 |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*]', ' ')) }
}
else { Write-Host '  the Phase B report is written separately.' }

Write-Host ''
Write-Host '  NOTHING WAS DISPATCHED. No SEC call, no EODHD call, no network request of any kind.'
Write-Host '  NOTHING WAS AUTHORISED. Every batch in the partition is unauthorised; the connector is off.'
Write-Host '  NOTHING WAS CONSUMED. The installed authorisation is still consumed 0, remaining 6.'
Write-Host '  THE RATE LIMITER WAS NOT MOVED. It remains the side-effecting gate inside the dispatch path.'

exit $code
