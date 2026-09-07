#requires -Version 5.1
<#
    PROVE THE INSTALLED SEC EDGAR AUTHORISATION LOADS AND AUTHORISES NOTHING. READ-ONLY.

    ZERO provider calls of any kind. No EODHD call, no SEC call, no price, split or dividend
    acquisition. No acquisition switch is set at any point in this script. NO SPLIT BATCH IS RUN
    and NO APPROVAL IS CHANGED.

    Nothing is written to the database: run, observation and quarantine counts are asserted
    unchanged, and every quarantined payload is asserted still quarantined under the same rule.
    No payload is reprocessed through the pipeline, released, reclassified or removed. The
    normaliser and SplitAdjustment are called directly, in memory, on bytes and lists, and their
    results are read and discarded. No price is repaired, interpolated or synthesised.

    NOTHING IS INSTALLED, AMENDED OR MIGRATED. Gate 6, its sealed declaration, CoverageEvaluation,
    the normaliser, the pipeline and SplitAdjustment are unchanged. One report is written:
    artifacts\verify\sec-edgar-six-member-install-report.md

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
    Remove-Item Env:\AIINV_POST_ACQUISITION -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_QUARANTINE_SURVEY -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_PAYLOAD_REREAD -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_MOVE_SCREEN -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_MOVE_CHARACTERISATION -ErrorAction SilentlyContinue
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
Write-Host '  READ-ONLY: nothing is acquired, nothing is written, no payload is released.'

$code = 0

try {

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecEdgarSixMemberInstallationTests' `
        -LogName 'install.log' `
        -Label 'the installed SEC EDGAR authorisation loads, is bounded to six, and authorises nothing' | Out-Host

    $code = [int]$LASTEXITCODE
}
finally { }

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'install-full.log' -Label 'full Release suite, connectors off' | Out-Host

    $code = [int]$LASTEXITCODE
}

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Clear-Switches

Write-Host ''
Write-Host '=== Installation outcome'

$report = Join-Path $root 'artifacts\verify\sec-edgar-six-member-install-report.md'

if (Test-Path $report) {
    Get-Content $report |
        Where-Object { $_ -match '^\| `|^\| \*\*|Breach pairs checked|causation is established|No relevant local event|supports coincidence' } |
        Select-Object -First 60 |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*]', ' ')) }
}
else { Write-Host '  install report is written separately.' }

Write-Host ''
Write-Host '  NOTHING WAS ACQUIRED. No EODHD call, no SEC call, no price, no split, no dividend.'
Write-Host '  NOTHING WAS RELEASED. Every quarantined payload is still quarantined, same rule.'
Write-Host '  NOTHING WAS INSTALLED OR AMENDED. Gate 6, the normaliser and SplitAdjustment are unchanged.'

exit $code
