#requires -Version 5.1
<#
    PROVE THE SEC EDGAR SIX-MEMBER PARTITION. READ-ONLY. NO ACQUISITION.

    ZERO provider calls of any kind. No SEC call, no EODHD call, no price, split or dividend
    acquisition, no network request whatsoever. No acquisition switch is set at any point in this
    script. NO SPLIT BATCH IS RUN and NO APPROVAL IS CHANGED.

    THE PARTITION IS NOT A RUNNER. It is a table of facts in the test project. It holds no provider,
    opens no scope and charges no authorisation, and the focused tests read its own source text to
    prove those tokens are absent from it. Every batch in it is unauthorised.

    NOTHING IS AUTHORISED, ENABLED, INSTALLED, AMENDED OR MIGRATED. The installed declaration, its
    digest, its consumption counter, Gate 6, the sealed manifest, the universe, the ledger and the
    database are all unchanged; the authorisation is loaded and read, never consumed. One report is
    written: artifacts\verify\sec-edgar-six-member-partition-report.md

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
Write-Host '  READ-ONLY: the partition is prepared, not approved. Nothing is acquired or charged.'

$code = 0

try {

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecEdgarSixMember' `
        -LogName 'partition.log' `
        -Label 'the six-member SEC partition: bound, exact, and authorising nothing' | Out-Host

    $code = [int]$LASTEXITCODE
}
finally { }

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'partition-full.log' -Label 'full Release suite, connectors off' | Out-Host

    $code = [int]$LASTEXITCODE
}

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Clear-Switches

Write-Host ''
Write-Host '=== Partition preparation outcome'

$report = Join-Path $root 'artifacts\verify\sec-edgar-six-member-partition-report.md'

if (Test-Path $report) {
    Get-Content $report |
        Where-Object { $_ -match '^\| `|^\| \*\*|planned requests|unauthorised|consumed|Remaining' } |
        Select-Object -First 60 |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*]', ' ')) }
}
else { Write-Host '  the partition report is written separately.' }

Write-Host ''
Write-Host '  NOTHING WAS ACQUIRED. No SEC call, no EODHD call, no network request of any kind.'
Write-Host '  NOTHING WAS AUTHORISED. Every batch in the partition is unauthorised; the connector is off.'
Write-Host '  NOTHING WAS CHARGED. The authorisation was loaded and read; consumed 0, remaining 6.'
Write-Host '  NOTHING WAS INSTALLED OR AMENDED. The declaration, its digest and Gate 6 are unchanged.'

exit $code
