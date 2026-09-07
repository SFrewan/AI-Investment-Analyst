#requires -Version 5.1
<#
    LEDGER AND PRICE-NAMESPACE FORENSICS. READ-ONLY.

    ZERO provider calls of any kind. No connector is switched on, so neither EODHD nor SEC can be
    reached from this script even by accident. Nothing is acquired: no prices, no splits, no
    dividends, no corporate actions, no scores, no orders. Nothing is written to the database -
    run, observation, execution and audit counts are asserted unchanged either side - and the
    sealed manifest is neither modified nor resealed.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot

function Clear-Switches {
    Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_LEDGER_FORENSICS -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_ACQUISITION_DRY_RUN -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_RECOVERY_CORRECTION -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SURVIVORSHIP_PROBE -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SURVIVORSHIP_PROBE_2 -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SEC_TICKER_PROBE -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_SEC_TICKER_REFINEMENT -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_PRICE_DEDUPLICATION -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_PRICE_SESSION_REMEDIATION -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_UNIVERSE_RETRY -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_UNIVERSE_READINESS -ErrorAction SilentlyContinue
}

Clear-Switches
$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if (Test-Path -Path $localSettings) { . $localSettings }
Clear-Switches

Write-Host ''
Write-Host '  NO connector is switched on. Neither EODHD nor SEC can be reached from this run.'
Write-Host '  READ-ONLY: nothing is acquired and nothing is written to the store.'

$code = 0

try {
    $env:AIINV_LEDGER_FORENSICS = '1'

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~LedgerForensicsTests' `
        -LogName 'ledger-forensics.log' `
        -Label 'ledger and price-namespace forensics, read-only' | Out-Host

    $code = [int]$LASTEXITCODE
}
finally {
    Remove-Item Env:\AIINV_LEDGER_FORENSICS -ErrorAction SilentlyContinue
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~CoverageEvaluationTests|FullyQualifiedName~PriceAcquisitionPlanningTests|FullyQualifiedName~IdentityResolutionTests|FullyQualifiedName~SecurityClassificationTests|FullyQualifiedName~ObservationDeduplicationTests|FullyQualifiedName~UsEquitySessionTests|FullyQualifiedName~CoverageRuleTests' `
        -LogName 'forensics-rules.log' `
        -Label 'every standing invariant, unchanged' | Out-Host

    $code = [int]$LASTEXITCODE
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'forensics-full.log' -Label 'full Release suite' | Out-Host

    $code = [int]$LASTEXITCODE
}

Clear-Switches

Write-Host ''
Write-Host '=== Forensics outcome'

$report = Join-Path $root 'artifacts\verify\ledger-forensics.md'

if (Test-Path $report) {
    Get-Content $report |
        Where-Object { $_ -match 'Ingestion runs|Runs against|Distinct request|Succeeded|Refused|Failed|archived|Audit records|Closing-price rows|Split rows|Distinct price series|^\| [A-E] -|already holds|Price rows already|Split rows already|Planned requests targeting|never requested|requested, succeeded|holding no price' } |
        Select-Object -First 40 |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) }
}
else { Write-Host '  no forensics report written.' }

Write-Host ''
Write-Host '  NOTHING WAS ACQUIRED. No EODHD call, no SEC call, no price, no split, no dividend.'

exit $code
