#requires -Version 5.1
<#
    PHASE A - REGULATORY-FILINGS NORMALIZER AND PRE-DISPATCH CONSUMPTION SAFETY.

    ZERO provider calls of any kind. No SEC call, no EODHD call, no network request whatsoever.
    The SEC connector is NOT enabled: both provider switches are forced false for this run, and the
    only connector these tests use is a stub whose FetchAsync throws.

    NO AUTHORIZATION IS CONSUMED. The consumption tests run against a synthetic declaration written
    to the temp directory and deleted afterwards; the installed SEC authorisation is loaded read-only
    and asserted at consumed 0 / remaining 6. No outcome artefact is written, which is the only thing
    that would persist consumption at all.

    NO BATCH IS AUTHORISED. Gate 6, the sealed manifest, the universe, the EODHD declarations, the
    price and split data, the ledger and the database are all untouched. One report is written:
    artifacts\verify\sec-edgar-phase-a-report.md

    NOTE ON THE CONNECTOR SWITCH. Clearing the environment overrides does NOT disable a connector:
    appsettings.Development.json sets Providers:Eodhd:Enabled to true and the test host runs in the
    Development environment. Both switches are therefore set to false EXPLICITLY.
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
Write-Host '  READ-ONLY: nothing is acquired, nothing is authorised, no unit is consumed.'

$code = 0

try {

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecEdgarFilingsNormalizerTests' `
        -LogName 'phasea-normalizer.log' `
        -Label 'the regulatory-filings normaliser: acceptance is publication, a future period is an attribute' | Out-Host

    $code = [int]$LASTEXITCODE
}
finally { }

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecEdgarPreDispatchConsumptionTests' `
        -LogName 'phasea-consumption.log' `
        -Label 'pre-dispatch consumption safety: a deterministic refusal costs nothing' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'phasea-full.log' -Label 'full Release suite, connectors off' | Out-Host

    $code = [int]$LASTEXITCODE
}

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Clear-Switches

Write-Host ''
Write-Host '=== Phase A outcome'

$report = Join-Path $root 'artifacts\verify\sec-edgar-phase-a-report.md'

if (Test-Path $report) {
    Get-Content $report |
        Where-Object { $_ -match '^\| `|^\| \*\*|consumed|Remaining|network|normaliser' } |
        Select-Object -First 60 |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*]', ' ')) }
}
else { Write-Host '  the Phase A report is written separately.' }

Write-Host ''
Write-Host '  NOTHING WAS ACQUIRED. No SEC call, no EODHD call, no network request of any kind.'
Write-Host '  NOTHING WAS AUTHORISED. Every batch in the partition is unauthorised; the connector is off.'
Write-Host '  NOTHING WAS CONSUMED. The installed authorisation is still consumed 0, remaining 6.'
Write-Host '  NOTHING WAS INSTALLED OR AMENDED. The declaration, its digest and Gate 6 are unchanged.'

exit $code
