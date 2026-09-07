#requires -Version 5.1
<#
    REMEDIATION REFINEMENT.

    Two halves, and only the first is allowed a provider call:

      1. the probe refinement - at most 4 FREE SEC requests, ceiling enforced by the transport,
         and skipped entirely once its report exists so a re-run cannot spend four more;
      2. everything else - the price de-duplication, the gates and the pure rules, which make
         no call of any kind.

    The EODHD connector is never switched on, so no billable call is possible from this script.
    NO prices. NO splits. NO dividends. NO corporate actions. NO scoring. NO orders.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot

function Clear-Switches {
    Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
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

if ([string]::IsNullOrWhiteSpace($env:AIINV_SEC_CONTACT)) {
    Write-Host '  STOPPING. AIINV_SEC_CONTACT is not set and EDGAR fair access requires a contact.'
    exit 2
}

Write-Host ''
Write-Host '  EODHD connector NOT switched on. No billable call is possible from this run.'
Write-Host '  SEC refinement ceiling: 4 free requests, enforced by the transport.'

$code = 0

# ---- 1. the refinement: the only provider calls this stage may make --------------------------
$refinement = Join-Path $root 'artifacts\verify\sec-ticker-refinement.md'

if (Test-Path $refinement) {
    Write-Host ''
    Write-Host '  Refinement already run and its report is on disk. Skipping: the four authorised'
    Write-Host '  SEC calls are spent and this script will not spend four more.'
}
else {
    try {
        $env:AIINV_SEC_TICKER_REFINEMENT = '1'

        & powershell -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
            -Filter 'FullyQualifiedName~SecTickerRefinementTests' `
            -LogName 'refinement.log' `
            -Label 'ticker probe refinement, at most 4 free SEC requests' | Out-Host

        $code = [int]$LASTEXITCODE
    }
    finally {
        Remove-Item Env:\AIINV_SEC_TICKER_REFINEMENT -ErrorAction SilentlyContinue
    }
}

# ---- 2. the price de-duplication: no provider call at all --------------------------------------
if ($code -eq 0) {
    try {
        $env:AIINV_PRICE_DEDUPLICATION = '1'

        & powershell -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
            -Filter 'FullyQualifiedName~PriceDuplicateRepairTests' `
            -LogName 'price-dedup.log' `
            -Label 'price de-duplication, zero provider calls' | Out-Host

        $code = [int]$LASTEXITCODE
    }
    finally {
        Remove-Item Env:\AIINV_PRICE_DEDUPLICATION -ErrorAction SilentlyContinue
    }
}

# ---- 3. the gates, re-judged with Gate 12 covering both namespaces ------------------------------
if ($code -eq 0) {
    try {
        $env:AIINV_UNIVERSE_READINESS = '1'

        & powershell -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
            -Filter 'FullyQualifiedName~UniverseReadinessTests' `
            -LogName 'gates.log' `
            -Label 'the twelve gates, de-duplication now across both namespaces' | Out-Host

        $code = [int]$LASTEXITCODE
    }
    finally {
        Remove-Item Env:\AIINV_UNIVERSE_READINESS -ErrorAction SilentlyContinue
    }
}

# ---- 4. the pure rules ---------------------------------------------------------------------------
if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~ObservationDeduplicationTests|FullyQualifiedName~UsEquitySessionTests|FullyQualifiedName~CoverageRuleTests|FullyQualifiedName~PriceAcquisitionPlanningTests|FullyQualifiedName~IdentityResolutionTests' `
        -LogName 'rules.log' `
        -Label 'de-duplication, session, coverage, planning and identity invariants' | Out-Host

    $code = [int]$LASTEXITCODE
}

# ---- 5. the whole suite ---------------------------------------------------------------------------
if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'remediation3-full.log' -Label 'full Release suite' | Out-Host

    $code = [int]$LASTEXITCODE
}

Clear-Switches

foreach ($pair in @(
    @('Refinement', 'artifacts\verify\sec-ticker-refinement.md', 'SEC calls|Succeeded|trading symbol|genuinely absent|EODHD|settles'),
    @('Price de-duplication', 'artifacts\verify\price-deduplication.md', 'Seam outcome|Duplicate groups|Rows to remove|Closing-price rows|Distinct price identities|PASS|FAIL'),
    @('Gates', 'artifacts\verify\universe-identity-final.md', 'De-duplication|Survivorship|Request budget|PASS|FAIL|deferred'))) {

    Write-Host ''
    Write-Host ('=== ' + $pair[0])

    $path = Join-Path $root $pair[1]
    if (Test-Path $path) {
        Get-Content $path |
            Where-Object { $_ -match $pair[2] } |
            Select-Object -First 20 |
            ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) }
    }
    else { Write-Host '  no report written.' }
}

exit $code
