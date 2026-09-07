#requires -Version 5.1
<#
    THE SEC EDGAR FUNDAMENTALS BACKFILL.

    This makes real requests to a U.S. government service. EDGAR is public domain and free, so
    nothing here is billable - but it is rate-limited and governed by a fair-access policy, so the
    run identifies itself, stays below the published ceiling, and asks once per company.

    Creates no opportunity, no prediction and no order. Starts no cycle. Changes no safety rule.
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
$log = Join-Path $out 'sec-backfill-run.txt'
$buildLog = Join-Path $out 'sec-backfill-build.log'
$testLog = Join-Path $out 'sec-backfill.log'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }
function Stamp { return ((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z') }

Say '==============================================================='
Say ' SEC EDGAR - FUNDAMENTALS BACKFILL'
Say (' started : ' + (Stamp))
Say ' 20 companies, one companyfacts request each, free and idempotent'
Say ' No opportunity. No prediction. No trade.'
Say '==============================================================='
Save-Log

# ---- local settings, which carry the contact address ----------------------

$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if (Test-Path -Path $localSettings) { . $localSettings }

if ([string]::IsNullOrWhiteSpace($env:AIINV_SEC_CONTACT)) {
    Say ''
    Say ' STOPPING before any request. AIINV_SEC_CONTACT is not set.'
    Say ' EDGAR fair access requires every request to name a contact, and this run'
    Say ' will not invent one. Set it in scripts/verify.local.ps1 (git-ignored).'
    Save-Log
    exit 2
}

Say ''
Say '  contact address configured (value not printed).'
Save-Log

# ---- reachability, before asking for anything -----------------------------

Say ''
Say '--- reachability pre-flight (no data requested)'
Save-Log

[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

$reachable = $false
$attempts = 8
$pause = 15

for ($attempt = 1; $attempt -le $attempts; $attempt++) {
    try {
        # A GET of a real document, with the three headers the SEC's fair-access page asks for.
        # data.sec.gov serves nothing at its root and answers HEAD with 403, so probing the root
        # tests the probe rather than the service - which is exactly what the first run of this
        # script did. This asks for one small submissions document instead: the same entitlement
        # the backfill uses, one request, and unambiguous when it works.
        $headers = @{
            'User-Agent'      = ('AI-Investment-Analyst ' + $env:AIINV_SEC_CONTACT)
            'Accept-Encoding' = 'gzip, deflate'
            'Accept'          = 'application/json'
        }

        $probe = Invoke-WebRequest -Uri 'https://data.sec.gov/submissions/CIK0000320193.json' -Method Get -TimeoutSec 30 -UseBasicParsing -Headers $headers

        if ([int]$probe.StatusCode -eq 200) {
            Say ('  attempt ' + [string]$attempt + ': reachable, status 200')
            $reachable = $true
            break
        }

        Say ('  attempt ' + [string]$attempt + ': unexpected status ' + [string]$probe.StatusCode)
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
    Say ' STOPPING before any request. Re-run when the network is back.'
    Save-Log
    exit 3
}

# ---- build, so the run cannot execute a stale assembly --------------------

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
    Say ' STOPPING before any request.'
    Save-Log
    exit 4
}

Say '  PASS  build'
Save-Log

# ---- the run --------------------------------------------------------------

Say ''
Say ('--- backfill   [' + (Stamp) + ']')
Say '  THIS IS THE STEP THAT REACHES THE SEC.'
Save-Log

$env:AIINV_SEC_BACKFILL = '1'

# The connector reads its own section while the container is being built, which is earlier than a
# test factory's in-memory settings can reach. appsettings.Development.json pins Enabled to false
# deliberately, and environment variables outrank it in the default configuration order - so this
# is where the connector is switched on for the duration of one run, and switched off again below.
$env:Providers__SecEdgar__Enabled = 'true'
$env:Providers__SecEdgar__ApplicationName = 'AI-Investment-Analyst'
$env:Providers__SecEdgar__ContactEmail = $env:AIINV_SEC_CONTACT
$env:Providers__SecEdgar__MaxRequestsPerSecond = '5'

$code = 0

try {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    & dotnet test (Join-Path $root 'tests\AI.Investment.Api.Tests') `
        -c Release --no-build --nologo `
        --filter 'FullyQualifiedName~SecBackfillTests' 2>&1 |
        Tee-Object -FilePath $testLog | Out-Null

    $code = $LASTEXITCODE
    $ErrorActionPreference = $previous
}
catch {
    $code = 1
    Say ('  run threw: ' + $_.Exception.Message)
}
finally {
    Remove-Item Env:\AIINV_SEC_BACKFILL -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__SecEdgar__Enabled -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__SecEdgar__ApplicationName -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__SecEdgar__ContactEmail -ErrorAction SilentlyContinue
    Remove-Item Env:\Providers__SecEdgar__MaxRequestsPerSecond -ErrorAction SilentlyContinue
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
Say ('  report     : ' + (Join-Path $out 'sec-backfill.md'))
Say ('  finished   : ' + (Stamp))
Save-Log

Write-Host ''
Write-Host ('Written: ' + $log)
exit $code
