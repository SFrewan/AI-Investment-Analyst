#requires -Version 5.1
<#
    LOCAL ARCHIVED-PAYLOAD RECOVERY - WIRE.US ONLY. THE LAST OF THE FIVE.

    THIS SCRIPT WRITES.

    What it does NOT do: no EODHD call, no SEC call, no acquisition, no acquisition authorisation,
    no new ingestion run, no second replay, no retry, no CCF / EVBG / GPP / VLDR replay.
    Gate 6, SplitAdjustment, the declarations and the sealed manifest are not touched.

    What it does: builds tools\replay-recover (net8.0) and runs it once for WIRE.US. The tool
    re-verifies every precondition against the live ai_investment database - including the two that
    pin the archive to the state the read-only final preflight measured - and STOPS before the
    replay if any of them fails. If they all pass it calls
    ArchivedPayloadReplayService.ReplayAsync exactly once, under the ORIGINAL ingestion run's
    identity, and then reads back what changed.

    Original ingestion run: bc0eb767-473d-481d-ad3d-d9d9bc491c50

    The symbol is passed explicitly and the tool refuses anything that is not one of the five.
    CCF, EVBG, GPP and VLDR cannot be replayed again by this path: their subjects now hold
    observations and their idempotency keys are claimed, so preconditions 12 and 13 would abort
    before any write.

    AFTER THIS RUN THERE IS NOTHING FURTHER TO RECOVER BY THIS PATH. The five [] members are
    quarantined under market-data.empty-series@1, which row-level quarantine does not address.

    THE CONNECTION STRING IS NEVER PRINTED.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $root 'tools\replay-recover\ReplayRecover.csproj'
$log = Join-Path $root 'artifacts\verify\recovery-wire-build.log'

$null = New-Item -ItemType Directory -Force -Path (Join-Path $root 'artifacts\verify')

Write-Host ''
Write-Host '  ================================================================'
Write-Host '   ARCHIVED PRICE RECOVERY - WIRE.US ONLY  (the last of the five)'
Write-Host '   ONE replay, ORIGINAL run identity, archived bytes.'
Write-Host '   NO provider call. NO acquisition authorisation. NO new run.'
Write-Host '   CCF, EVBG, GPP and VLDR are NOT replayed.'
Write-Host '  ================================================================'
Write-Host ''

$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES) -and (Test-Path -Path $localSettings)) {
    . $localSettings
}

if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES)) {
    Write-Host '  STOPPING. AIINV_TEST_POSTGRES is not set.'
    exit 3
}

Write-Host '  Database configured (value not printed).'
Write-Host ''

if (-not (Test-Path -LiteralPath $tool)) {
    Write-Host ('  STOPPING. The tool project was not found: ' + $tool)
    exit 4
}

Write-Host '--- toolchain'
Write-Host ('  dotnet --version : ' + (& dotnet --version))
Write-Host '  target framework : net8.0'
Write-Host ''

# Both connectors pinned off for the whole run. Nothing in this path can reach a provider, and
# leaving them enabled would invite an accident with nothing to do with the recovery.
$env:Providers__Eodhd__Enabled = 'false'
$env:Providers__SecEdgar__Enabled = 'false'

Write-Host '--- building the tool (Release, net8.0)'

& dotnet build $tool -c Release --nologo 2>&1 | Tee-Object -FilePath $log | Out-Null
$code = $LASTEXITCODE

if ($code -ne 0) {
    Write-Host '  FAIL  the tool did not build. NOTHING WAS RECOVERED.'
    Write-Host ''

    foreach ($line in (Get-Content -Path $log -Tail 60)) {
        if ($line -match 'error|Build FAILED') { Write-Host ('  ' + $line.Trim()) }
    }

    Write-Host ''
    Write-Host ('  Full build log: ' + $log)
    exit 6
}

Write-Host '  PASS  built.'
Write-Host ''

$exe = Join-Path $root 'tools\replay-recover\bin\Release\net8.0\replay-recover.exe'

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host ('  STOPPING. The built executable was not found: ' + $exe)
    exit 7
}

& $exe $root 'WIRE.US'
$code = $LASTEXITCODE

Remove-Item Env:\Providers__Eodhd__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '  WIRE.US ONLY. CCF, EVBG, GPP and VLDR were not replayed again.'
Write-Host '  The five [] members were not touched. NOTHING FURTHER IS STARTED.'

exit $code
