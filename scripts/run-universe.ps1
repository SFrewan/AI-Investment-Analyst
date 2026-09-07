#requires -Version 5.1
<#
    THE FOUR-HUNDRED-COMPANY POINT-IN-TIME UNIVERSE.

    Four stages, in order, each resumable:

      1. frames    - 7 free EDGAR cross-section requests; builds the roster
      2. profiles  - 400 free EDGAR submissions requests; ticker, SIC, name
      3. facts     - 400 free EDGAR companyfacts requests; the evidence base
      4. manifest  - exactly 2 billable EODHD symbol-list calls; seals and judges

    Authorised budget: ~821 free SEC calls + 2 billable EODHD calls.

    This does NOT start the price/splits acquisition, does NOT fetch price history, does NOT
    declare or score any strategy, and does NOT trade. Every stage is gated on its own environment
    variable and each is set here and cleared again immediately afterwards.

    A stage that has already run is cheap to repeat: the ingestion ledger and the observation store
    are both consulted before a request is built, so a rerun after a failure resumes rather than
    re-spends.
#>

[CmdletBinding()]
param([switch]$SkipFullSuite)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out

function Say([string]$text) { Write-Host $text }

# ---- local settings, which carry the contact address and the test database ----------------
#
# Dot-sourced unconditionally rather than only when the database variable is empty. The contact
# address lives in the same file, and a shell that happens to have the database variable set would
# otherwise skip the file and take every EDGAR stage with it.

$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if (Test-Path -Path $localSettings) { . $localSettings }

if ([string]::IsNullOrWhiteSpace($env:AIINV_SEC_CONTACT)) {
    Say ''
    Say '  STOPPING before any request. AIINV_SEC_CONTACT is not set.'
    Say '  EDGAR fair access requires every request to name a contact, and this run'
    Say '  will not invent one. Set it in scripts/verify.local.ps1 (git-ignored).'
    exit 2
}

# ---- switching the EDGAR connector on, and why it has to happen HERE ----------------------
#
# The connector reads its own configuration section while the container is being BUILT, which is
# earlier than a test factory's in-memory settings can reach: appsettings.Development.json pins
# Enabled to false deliberately, and by the time a WebApplicationFactory's ConfigureAppConfiguration
# callbacks are applied, AddInfrastructure has already read the section and decided not to register
# the connector. Environment variables outrank appsettings in the default configuration order and
# are in place before the entry point runs, so this is where the connector is switched on - for the
# duration of this run, and switched off again at the end.
#
# The symptom when this is missing is not a missing connector, it is a stale-looking registry: every
# request refuses with source.supplies-category naming a category count, because seeding had no
# definition to reconcile the stored row against. run-sec-backfill.ps1 has always done this; this
# script now does too.

$env:Providers__SecEdgar__Enabled = 'true'
$env:Providers__SecEdgar__ApplicationName = 'AI-Investment-Analyst'
$env:Providers__SecEdgar__ContactEmail = $env:AIINV_SEC_CONTACT
$env:Providers__SecEdgar__MaxRequestsPerSecond = '5'

Say ''
Say '  contact address configured (value not printed).'
Say '  EDGAR connector switched on for this run.'

if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES)) {
    Say '  WARNING: AIINV_TEST_POSTGRES is not set. Database-backed stages will SKIP.'
}
else {
    Say '  integration database configured (value not printed).'
}

# ---- the stages ----------------------------------------------------------------------------

# Out-Host, not a bare call, and the reason is worth a line.
#
# A function's return value in PowerShell is EVERYTHING it wrote to the pipeline. Calling the child
# script bare put all of its output - every test line - into the function's return value, so the
# caller received an array whose last element happened to be the exit code, `-ne 0` filtered the
# array instead of comparing a number, and the run reported a stage as stopped while exiting 0 and
# printing none of the output that would have explained it. Out-Host sends the child's output to the
# console where it belongs and leaves the pipeline empty, so the only thing returned is the code.
function Invoke-Stage([string]$Variable, [string]$Filter, [string]$LogName, [string]$Label) {
    Set-Item -Path ("Env:\" + $Variable) -Value '1'

    try {
        & powershell -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
            -Filter $Filter -LogName $LogName -Label $Label | Out-Host

        return [int]$LASTEXITCODE
    }
    finally {
        Remove-Item -Path ("Env:\" + $Variable) -ErrorAction SilentlyContinue
    }
}

$stages = @(
    @{ Var = 'AIINV_UNIVERSE_FRAMES';   Filter = 'FullyQualifiedName~UniverseFramesTests';
       Log = 'universe-frames.log';     Label = '1/4 frames: 7 free cross-section requests' },

    @{ Var = 'AIINV_UNIVERSE_PROFILES'; Filter = 'FullyQualifiedName~UniverseProfileTests';
       Log = 'universe-profiles.log';   Label = '2/4 profiles: 400 free submissions requests' },

    @{ Var = 'AIINV_UNIVERSE_FACTS';    Filter = 'FullyQualifiedName~UniverseFactsTests';
       Log = 'universe-facts.log';      Label = '3/4 facts: 400 free companyfacts requests' },

    @{ Var = 'AIINV_UNIVERSE_MANIFEST'; Filter = 'FullyQualifiedName~UniverseManifest400Tests';
       Log = 'universe-manifest.log';   Label = '4/4 manifest: 2 billable calls, seal, twelve gates' }
)

$code = 0

foreach ($stage in $stages) {
    Say ''
    Say ('=== ' + $stage.Label + '  started ' + (Get-Date).ToUniversalTime().ToString('u'))

    $code = Invoke-Stage $stage.Var $stage.Filter $stage.Log $stage.Label

    if ($code -ne 0) {
        Say ''
        Say ('  STOPPED at ' + $stage.Label + '. Nothing further was requested.')
        Say ('  Log: artifacts\verify\' + $stage.Log)

        # break, not exit: the connector has to be switched off again on the way out, and an early
        # exit from inside the loop would leave it enabled in whatever shell ran this.
        break
    }
}

if ($code -eq 0 -and -not $SkipFullSuite) {
    Say ''
    Say ('=== full Release suite  started ' + (Get-Date).ToUniversalTime().ToString('u'))

    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'gate-tests.ps1') `
        -Filter 'FullyQualifiedName~AI.Investment' `
        -LogName 'universe-full.log' -Label 'full Release suite'

    $code = $LASTEXITCODE
}

# ---- what the manifest said ------------------------------------------------------------------

Say ''
Say '=== The twelve gates'

$manifestReport = Join-Path $out 'universe-manifest-400.md'

if (Test-Path $manifestReport) {
    Get-Content $manifestReport |
        Where-Object { $_ -match 'PASS|FAIL|deferred|fingerprint|Members sealed|Unmatched|calls spent|Stopped filing|Absent from' } |
        ForEach-Object { Write-Host ('  ' + ($_ -replace '[`*|]', ' ')) }
}
else {
    Say '  no manifest report was written.'
}

# Switched off again. A connector left enabled in the shell that ran this would be enabled for
# whatever anyone runs next, which is not a decision this script gets to make on their behalf.
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__ApplicationName -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__ContactEmail -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__MaxRequestsPerSecond -ErrorAction SilentlyContinue

Say ''
Say ('finished: ' + (Get-Date).ToUniversalTime().ToString('u'))

exit $code
