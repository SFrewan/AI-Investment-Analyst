#requires -Version 5.1
<#
    THE AUTHORISED EODHD ACQUISITION.

    This script SPENDS PROVIDER REQUESTS - at most 704, the ceiling in
    declarations/acquisition-eodhd-sample400.json, enforced in code by AcquisitionAuthorization.

    It is written to be run once. If artifacts/universe/acquisition-outcome.json already exists the
    acquisition step is skipped entirely, so a second run of this script cannot spend anything.

    SEC is switched OFF throughout. EODHD is switched ON for the acquisition step ONLY, and OFF
    again for verification and for the full suite - so every other step is provably unable to
    reach a provider.

    NO dividends. NO SPY. NO symbol outside the authorised 355. NO widened window.
    The sealed manifest is neither modified nor resealed.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot

function Clear-Switches {
    Remove-Item Env:\AIINV_ACQUISITION_EXECUTE -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_POST_ACQUISITION -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_ACQUISITION_AUDIT -ErrorAction SilentlyContinue
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

# SEC off for the whole script, stated rather than assumed.
$env:Providers__SecEdgar__Enabled = 'false'
$env:Providers__Eodhd__Enabled = 'false'

$outcome = Join-Path $root 'artifacts\universe\acquisition-outcome.json'
$code = 0

# ---- 1. the authorisation and every standing invariant, with both connectors off ------------------

& powershell -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
    -Filter 'FullyQualifiedName~AcquisitionAuthorizationLedgerTests|FullyQualifiedName~CoverageEvaluationTests|FullyQualifiedName~PriceAcquisitionPlanningTests|FullyQualifiedName~IdentityResolutionTests|FullyQualifiedName~SecurityClassificationTests|FullyQualifiedName~ObservationDeduplicationTests|FullyQualifiedName~UsEquitySessionTests|FullyQualifiedName~CoverageRuleTests' `
    -LogName 'acquire-rules.log' `
    -Label 'the authorisation and every standing invariant, connectors off' | Out-Host

$code = [int]$LASTEXITCODE

# ---- 2. the acquisition itself. EODHD on, and only here. -----------------------------------------

if ($code -ne 0) {
    Write-Host ''
    Write-Host '  STOPPING before the acquisition: an invariant failed. Nothing was spent.'
}
elseif (Test-Path $outcome) {
    Write-Host ''
    Write-Host '  The acquisition has already run and its outcome is on disk. Skipping:'
    Write-Host '  the authorised requests are spent and this script will not spend them again.'
}
else {
    if ([string]::IsNullOrWhiteSpace($env:Providers__Eodhd__ApiKey)) {
        Write-Host ''
        Write-Host '  NOTE: no EODHD key in the process environment. The host also reads the'
        Write-Host '  user-secrets store and the per-user environment, which is where it has been.'
    }

    Write-Host ''
    Write-Host '  ================================================================'
    Write-Host '   SPENDING UP TO 704 AUTHORISED EODHD REQUESTS.'
    Write-Host '   355 symbols, eod then splits, 2021-09-01..2026-08-31.'
    Write-Host '   Paced at roughly one a second against a declared 60 a minute,'
    Write-Host '   so expect this step to take 15-25 minutes. Do not interrupt it:'
    Write-Host '   the record of what was spent is written when it finishes.'
    Write-Host '  ================================================================'
    Write-Host ''

    try {
        $env:Providers__Eodhd__Enabled = 'true'
        $env:AIINV_ACQUISITION_EXECUTE = '1'

        & powershell -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
            -Filter 'FullyQualifiedName~AcquisitionExecutionTests' `
            -LogName 'acquisition-execute.log' `
            -Label 'the authorised acquisition, at most 704 EODHD requests' | Out-Host

        $code = [int]$LASTEXITCODE
    }
    finally {
        Remove-Item Env:\AIINV_ACQUISITION_EXECUTE -ErrorAction SilentlyContinue
        $env:Providers__Eodhd__Enabled = 'false'
    }
}

# ---- 3. verification, with EODHD off again --------------------------------------------------------

if (Test-Path $outcome) {
    try {
        $env:Providers__Eodhd__Enabled = 'false'
        $env:AIINV_POST_ACQUISITION = '1'

        & powershell -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
            -Filter 'FullyQualifiedName~PostAcquisitionVerificationTests' `
            -LogName 'post-acquisition.log' `
            -Label 'post-acquisition verification, read-only, connectors off' | Out-Host

        if ($code -eq 0) { $code = [int]$LASTEXITCODE }
    }
    finally {
        Remove-Item Env:\AIINV_POST_ACQUISITION -ErrorAction SilentlyContinue
    }
}

# ---- 4. the full suite ----------------------------------------------------------------------------

$env:Providers__Eodhd__Enabled = 'false'

& powershell -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
    -Filter 'FullyQualifiedName~AI.Investment' `
    -LogName 'acquire-full.log' -Label 'full Release suite, connectors off' | Out-Host

if ($code -eq 0) { $code = [int]$LASTEXITCODE }

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Clear-Switches

Write-Host ''
Write-Host '=== Acquisition outcome'

$acquired = Join-Path $root 'artifacts\verify\acquisition-outcome.md'
$verified = Join-Path $root 'artifacts\verify\post-acquisition-verification.md'

foreach ($file in @($acquired, $verified)) {
    if (Test-Path $file) {
        Write-Host ''
        Write-Host ("--- " + (Split-Path -Leaf $file))
        Get-Content $file |
            Where-Object { $_ -match 'Planned|Suppressed|Dispatched|succeeded|failed|refused|Authorisation|Observations|quarantin|stopped|Gate |bytes against|Matches the approved|Acquisition-ready|Excluded|outside|wrong|adjusted|excluded member|re-planned|would suppress|would still dispatch|Distinct price series|Split rows|Closing-price rows' } |
            Select-Object -First 45 |
            ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) }
    }
}

exit $code
