#requires -Version 5.1
<#
    BLOCK 2B - THE CONTROLLED HISTORICAL BACKFILL.

    THIS MAKES REAL, BILLABLE PROVIDER CALLS: roughly two per instrument that is not already
    covered, for twenty instruments. It is idempotent - a rerun skips anything the ingestion
    ledger already records as complete - so the cost of running it twice is close to nothing.

    Starts no cycle, waits for no cooldown, and changes no safety rule.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'AI-Investment-Analyst.sln'
$out = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log = Join-Path $out 'backfill-run.txt'
$buildLog = Join-Path $out 'backfill-build.log'
$testLog = Join-Path $out 'backfill.log'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }
function Stamp { return ((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z') }

Say '==============================================================='
Say ' BLOCK 2B - CONTROLLED HISTORICAL BACKFILL'
Say (' started : ' + (Stamp))
Say ' 20 instruments, 2 years, splits before prices, idempotent'
Say '==============================================================='
Save-Log

# ---- provider reachability, before spending anything -----------------------

Say ''
Say '--- reachability pre-flight (no token, no query string, no data)'

# Retried, because this machine's resolver is a virtual-switch gateway that intermittently fails
# to answer for a few seconds at a time - observed resolving, then failing, then resolving again
# inside four minutes. A DNS lookup and an unauthenticated HEAD cost nothing and reach no API, so
# waiting a couple of minutes for the resolver to come back is strictly better than either
# abandoning a run that would have worked or starting one that fails twenty times at the wire.

$reachable = $false
$attempts = 8
$pause = 15

[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

for ($attempt = 1; $attempt -le $attempts; $attempt++) {
    try {
        $probe = Invoke-WebRequest -Uri 'https://eodhd.com' -Method Head -TimeoutSec 20 -UseBasicParsing
        Say ('  attempt ' + [string]$attempt + ': reachable, status ' + [string]$probe.StatusCode)
        $reachable = $true
        break
    }
    catch {
        Say ('  attempt ' + [string]$attempt + ' of ' + [string]$attempts + ': ' + $_.Exception.Message)
    }

    if ($attempt -lt $attempts) {
        Save-Log
        Start-Sleep -Seconds $pause
    }
}

Save-Log

if (-not $reachable) {
    Say ''
    Say ' STOPPING before any call. A backfill against an unreachable provider would record'
    Say ' twenty failures and prove nothing. Re-run when the network is back.'
    Save-Log
    exit 2
}

# ---- build, so the run below cannot execute a stale assembly ---------------

Say ''
Say ('--- Release build (warnings are errors)   [' + (Stamp) + ']')
Save-Log

$buildCode = 0

try {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    & dotnet build $sln -c Release --nologo 2>&1 |
        Tee-Object -FilePath $buildLog | Out-Null

    $buildCode = $LASTEXITCODE
    $ErrorActionPreference = $previous
}
catch {
    $buildCode = 1
    Say ('  build threw: ' + $_.Exception.Message)
}

if ($buildCode -ne 0) {
    Say ('  FAIL  build (exit ' + [string]$buildCode + ')')
    $errs = @()
    if (Test-Path $buildLog) {
        $errs = @(Get-Content -Path $buildLog | Where-Object { $_ -match 'error [A-Z]+[0-9]+' } | Select-Object -First 25)
    }
    foreach ($e in $errs) { Say ('    ' + $e.Trim()) }
    Say ''
    Say ' STOPPING before any call. Nothing was fetched and nothing was billed.'
    Say ('  build log  : ' + $buildLog)
    Save-Log
    exit 3
}

Say '  PASS  build'
Save-Log

# ---- the run ---------------------------------------------------------------

Say ''
Say ('--- backfill   [' + (Stamp) + ']')
Say '  THIS IS THE BILLABLE STEP.'
Save-Log

$env:AIINV_BACKFILL = '1'
$code = 0

try {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    & dotnet test (Join-Path $root 'tests\AI.Investment.Api.Tests') `
        -c Release --no-build --nologo `
        --filter 'FullyQualifiedName~HistoryBackfillTests' 2>&1 |
        Tee-Object -FilePath $testLog | Out-Null

    $code = $LASTEXITCODE
    $ErrorActionPreference = $previous
}
catch {
    $code = 1
    Say ('  run threw: ' + $_.Exception.Message)
}
finally {
    Remove-Item Env:\AIINV_BACKFILL -ErrorAction SilentlyContinue
}

$tail = @()
if (Test-Path $testLog) { $tail = @(Get-Content -Path $testLog -Tail 80) }

foreach ($t in $tail) {
    if ($t -match 'Passed!|Failed!|Passed:|Failed:|Skipped:|error |Assert\.') {
        Say ('  ' + $t.Trim())
    }
}

if ($code -eq 0) { Say '  PASS  backfill' } else { Say ('  FAIL  backfill (exit ' + [string]$code + ')') }

Say ''
Say ('  run log    : ' + $testLog)
Say ('  report     : ' + (Join-Path $out 'backfill.md'))
Say ('  finished   : ' + (Stamp))
Save-Log

Write-Host ''
Write-Host ('Written: ' + $log)
exit $code
