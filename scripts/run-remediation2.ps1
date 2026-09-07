#requires -Version 5.1
<#
    PRE-ACQUISITION REMEDIATION.

    Two halves, and only the first half is allowed a provider call:

      1. the identity probe - at most 5 FREE SEC requests, ceiling enforced by the transport;
      2. everything else - the session re-stamp, the coverage rule and the pure tests, which
         make no call of any kind.

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
Write-Host '  SEC probe ceiling: 5 free requests, enforced by the transport.'

$code = 0

# ---- 1. the probe: the only provider calls this stage may make -------------------------------
# Its five calls are authorised ONCE. A report on disk means they have been spent, and re-running
# the script must not spend five more - so the presence of the artifact is the guard, not care.
$probeReport = Join-Path $root 'artifacts\verify\sec-ticker-probe.md'

if (Test-Path $probeReport) {
    Write-Host ''
    Write-Host '  Probe already run and its report is on disk. Skipping: the five authorised'
    Write-Host '  SEC calls are spent and this script will not spend five more.'
}
else {
try {
    $env:AIINV_SEC_TICKER_PROBE = '1'

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~SecTickerProbeTests' `
        -LogName 'probe.log' `
        -Label 'historical ticker probe, at most 5 free SEC requests' | Out-Host

    $code = [int]$LASTEXITCODE
}
finally {
    Remove-Item Env:\AIINV_SEC_TICKER_PROBE -ErrorAction SilentlyContinue
}
}

# ---- 2. the session re-stamp: no provider call at all -----------------------------------------
if ($code -eq 0) {
    try {
        $env:AIINV_PRICE_SESSION_REMEDIATION = '1'

        & powershell -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
            -Filter 'FullyQualifiedName~PriceSessionRemediationTests' `
            -LogName 'session-restamp.log' `
            -Label 'session-close re-stamp, zero provider calls' | Out-Host

        $code = [int]$LASTEXITCODE
    }
    finally {
        Remove-Item Env:\AIINV_PRICE_SESSION_REMEDIATION -ErrorAction SilentlyContinue
    }
}

# ---- 3. the pure rules ------------------------------------------------------------------------
if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~UsEquitySessionTests|FullyQualifiedName~CoverageRuleTests|FullyQualifiedName~PriceAcquisitionPlanningTests|FullyQualifiedName~IdentityResolutionTests' `
        -LogName 'remediation-rules.log' `
        -Label 'session resolver, coverage rule, planning and identity invariants' | Out-Host

    $code = [int]$LASTEXITCODE
}

# ---- 4. the whole suite ------------------------------------------------------------------------
if ($code -eq 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'remediation2-full.log' -Label 'full Release suite' | Out-Host

    $code = [int]$LASTEXITCODE
}

Clear-Switches

Write-Host ''
Write-Host '=== Probe outcome'
$probe = Join-Path $root 'artifacts\verify\sec-ticker-probe.md'
if (Test-Path $probe) {
    Get-Content $probe |
        Where-Object { $_ -match 'SEC calls|Succeeded|trading symbol|EODHD calls|establishes|dead end|worth authorising|partial' } |
        Select-Object -First 20 |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) }
}
else { Write-Host '  no probe report written.' }

Write-Host ''
Write-Host '=== Session re-stamp outcome'
$restamp = Join-Path $root 'artifacts\verify\price-session-remediation.md'
if (Test-Path $restamp) {
    Get-Content $restamp |
        Where-Object { $_ -match 'Seam outcome|Mis-stamped|Closing-price rows|Corrected|Refused|Published|Distinct prices' } |
        Select-Object -First 20 |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) }
}
else { Write-Host '  no re-stamp report written.' }

exit $code
