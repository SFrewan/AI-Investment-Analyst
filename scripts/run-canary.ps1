#requires -Version 5.1
<#
    THE SEC CANARY. ONE COMPANY. ONE REQUEST. ONE AUTHORISATION UNIT.

    THIS STEP REACHES THE U.S. SECURITIES AND EXCHANGE COMMISSION.

    It runs exactly one test - the execution door itself - and nothing else. The filter names that
    one fact rather than its class, deliberately: the class also carries the at-rest facts that
    assert no batch is authorised, and those are true of the resting state rather than of this
    window. Running them here would report the approval as a failure.

    SCOPE, and nothing wider:
      * sec-edgar ONLY. No prices, no splits, no dividends, no benchmark, no scoring.
      * ONE company: CIK 0000892482, QUMU.US, the single member of batch 1.
      * ONE request: submissions/CIK0000892482.json. The connector declares supportsWindow=false,
        so the provider request carries NO from or to date. The authorisation's scope window
        2021-09-01..2026-08-31 is used for Covers() and is never sent to the provider.
      * At most ONE dispatch, against a ceiling of 6 that is enforced and NOT amended.

    EODHD is switched OFF throughout. Nothing here runs batch 2, 3, 4, 5 or 6.

    FAIR ACCESS. This refuses before anything if AIINV_SEC_CONTACT is not set, and never prints the
    value. The operator has confirmed the address is monitored and may be used.

    NO FULL SUITE RUNS HERE. The batch flag is returned to false first, and the suite is run after,
    so a suite result is never produced from a state that is not the resting one.
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

if ([string]::IsNullOrWhiteSpace($env:AIINV_SEC_CONTACT)) {
    Write-Host ''
    Write-Host '  STOPPING before any request. AIINV_SEC_CONTACT is not set.'
    Write-Host '  EDGAR fair access requires every request to name a contact, and this run'
    Write-Host '  will not invent one.'
    Write-Host ''
    exit 2
}

Write-Host ''
Write-Host '  contact address configured (value not printed).'

$env:Providers__Eodhd__Enabled = 'false'
$env:Providers__SecEdgar__Enabled = 'false'

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   SEC CANARY - BATCH 1 - QUMU.US - CIK 0000892482'
Write-Host '   ONE request to data.sec.gov. ONE authorisation unit.'
Write-Host '   submissions/CIK0000892482.json, RegulatoryFilings, NO window.'
Write-Host '   Ceiling 6, enforced and not amended. Batches 2-6 do NOT run.'
Write-Host '  ================================================================'
Write-Host ''

$code = 0

try {
    # The connector reads its section while the container is built, so the switch is set here for
    # the duration of this one step and cleared again below. appsettings.json still pins it false.
    $env:Providers__SecEdgar__Enabled = 'true'
    $env:Providers__SecEdgar__ApplicationName = 'AI-Investment-Analyst'
    $env:Providers__SecEdgar__ContactEmail = $env:AIINV_SEC_CONTACT
    $env:Providers__SecEdgar__MaxRequestsPerSecond = '5'

    $env:AIINV_SEC_BATCH = '1'
    $env:AIINV_SEC_BATCH_INDEX = '1'

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecFilingsBatchTests.One_approved_sec_batch_is_acquired_and_nothing_else_is' `
        -LogName 'sec-canary.log' `
        -Label 'SEC canary: batch 1, QUMU, exactly one EDGAR filing request' | Out-Host

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

Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Clear-Switches

$artefact = Join-Path $root 'artifacts\universe\acquisition-sec-01-attempt-01.json'

Write-Host ''
Write-Host '=== canary outcome'

if (Test-Path $artefact) {
    Write-Host ('  attempt record: ' + $artefact)
    Get-Content $artefact -Raw |
        ForEach-Object { $_ -replace '","', "`"`r`n  `"" } |
        Select-Object -First 1 |
        Out-Host
}
else { Write-Host '  NO ATTEMPT RECORD WAS WRITTEN. Read the log before doing anything else.' }

Write-Host ''
Write-Host '  NOTHING ELSE WAS ACQUIRED. No price, no split, no dividend request was made.'
Write-Host '  NO NEXT BATCH RAN. Batches 2-6 are unauthorised and each needs its own approval.'
Write-Host '  THE CEILING WAS NOT AMENDED. The declaration and its digest are unchanged.'
Write-Host '  THE BATCH FLAG IS RETURNED TO FALSE BEFORE THE FULL SUITE IS RUN.'

exit $code
