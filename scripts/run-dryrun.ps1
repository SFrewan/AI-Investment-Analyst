#requires -Version 5.1
<#
    PRE-ACQUISITION DRY RUN.

    ZERO provider calls of any kind. No connector is switched on, so neither EODHD nor SEC can be
    reached from this script even by accident. Nothing is acquired: no prices, no splits, no
    dividends, no corporate actions, no scores, no orders.

    It builds every request the acquisition WOULD send, computes their fingerprints, checks them
    against the ingestion ledger, evaluates gate 6's sealed rule against the prices that already
    exist, measures gate 4, and writes a manifest of what would be requested. The sealed universe
    manifest is asserted byte-identical either side.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot

function Clear-Switches {
    Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
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
Write-Host '  Nothing is acquired. This stage only computes what an acquisition would send.'

# --- the shapes this stage depends on, dumped so one build failure is one cycle ------------------

$probeDir = Join-Path $root 'artifacts\verify'
if (-not (Test-Path $probeDir)) { New-Item -ItemType Directory -Path $probeDir -Force | Out-Null }

$probe = Join-Path $probeDir 'api-probe.txt'
$src = Join-Path $root 'src'

Set-Content -Path $probe -Value '=== DateRange ===' -Encoding UTF8
& findstr /s /n /c:"record DateRange" /c:"class DateRange" /c:"DateRange Create" (Join-Path $src '*.cs') 2>&1 |
    Select-Object -First 20 | Add-Content -Path $probe -Encoding UTF8

Add-Content -Path $probe -Value '=== IngestionRequest.Create / Fingerprint ===' -Encoding UTF8
& findstr /s /n /c:"IngestionRequest Create" /c:"string Fingerprint" (Join-Path $src '*.cs') 2>&1 |
    Select-Object -First 20 | Add-Content -Path $probe -Encoding UTF8

Add-Content -Path $probe -Value '=== IngestionOutcome ===' -Encoding UTF8
& findstr /s /n /c:"enum IngestionOutcome" /c:"Succeeded" (Join-Path $src 'AI.Investment.Domain\*.cs') 2>&1 |
    Select-Object -First 20 | Add-Content -Path $probe -Encoding UTF8

Add-Content -Path $probe -Value '=== IngestionRun.Request / Outcome ===' -Encoding UTF8
& findstr /s /n /c:"IngestionRequest Request" /c:"IngestionOutcome Outcome" (Join-Path $src '*.cs') 2>&1 |
    Select-Object -First 20 | Add-Content -Path $probe -Encoding UTF8

Write-Host ''
Write-Host '=== API shapes this stage compiles against'
Get-Content $probe | ForEach-Object { Write-Host ('  ' + $_) }

# --- the dry run --------------------------------------------------------------------------------

$code = 0

# The pure coverage facts first: they need no container and no database, so a mistake in the
# sealed rule surfaces in seconds rather than after a full fixture spins up.
& powershell -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
    -Filter 'FullyQualifiedName~CoverageEvaluationTests|FullyQualifiedName~PriceAcquisitionPlanningTests|FullyQualifiedName~IdentityResolutionTests|FullyQualifiedName~SecurityClassificationTests|FullyQualifiedName~ObservationDeduplicationTests|FullyQualifiedName~UsEquitySessionTests|FullyQualifiedName~CoverageRuleTests' `
    -LogName 'dryrun-rules.log' `
    -Label 'the sealed coverage rule and every standing invariant' | Out-Host

$code = [int]$LASTEXITCODE

if ($code -eq 0) {
    try {
        $env:AIINV_ACQUISITION_DRY_RUN = '1'

        & powershell -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
            -Filter 'FullyQualifiedName~AcquisitionDryRunTests' `
            -LogName 'dryrun-plan.log' `
            -Label 'the acquisition dry run, zero provider calls' | Out-Host

        $code = [int]$LASTEXITCODE
    }
    finally {
        Remove-Item Env:\AIINV_ACQUISITION_DRY_RUN -ErrorAction SilentlyContinue
    }
}

if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'dryrun-full.log' -Label 'full Release suite' | Out-Host

    $code = [int]$LASTEXITCODE
}

Clear-Switches

Write-Host ''
Write-Host '=== Dry-run outcome'

$report = Join-Path $root 'artifacts\verify\price-acquisition-dry-run.md'

if (Test-Path $report) {
    Get-Content $report |
        Where-Object { $_ -match 'Members|Acquisition-ready|Excluded|requests|Would actually fetch|Already satisfied|Distinct|Faults|Gate 6|Gate 4|Mean|collisions|quarantine|planned symbols' } |
        Select-Object -First 40 |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) }
}
else { Write-Host '  no dry-run report written.' }

Write-Host ''
Write-Host '  NOTHING WAS ACQUIRED. No EODHD call, no SEC call, no price, no split, no dividend.'

exit $code
