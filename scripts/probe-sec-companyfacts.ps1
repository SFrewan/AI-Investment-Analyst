#requires -Version 5.1
<#
    READ-ONLY PROBE. Why do AAPL and HD fail where eighteen others succeed?

    Two GETs against the same endpoint the connector uses, with the same headers, reporting the
    status and the size. Requests nothing that the backfill has not already requested.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log = Join-Path $out 'sec-probe.md'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }

Say '# Why AAPL and HD fail'
Say ''
Say ('Generated ' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z. Read-only.')
Say ''

$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'
if (Test-Path -Path $localSettings) { . $localSettings }

if ([string]::IsNullOrWhiteSpace($env:AIINV_SEC_CONTACT)) {
    Say 'AIINV_SEC_CONTACT is not set; not probing without a contact address.'
    Save-Log
    exit 2
}

[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

$headers = @{
    'User-Agent' = ('AI-Investment-Analyst ' + $env:AIINV_SEC_CONTACT)
    'Accept'     = 'application/json'
}

$targets = @(
    @{ Ticker = 'AAPL'; Cik = '0000320193' },
    @{ Ticker = 'HD';   Cik = '0000060695' },
    @{ Ticker = 'KO';   Cik = '0000021344' }
)

foreach ($t in $targets) {
    $uri = 'https://data.sec.gov/api/xbrl/companyfacts/CIK' + $t.Cik + '.json'
    Say ''
    Say ('### ' + $t.Ticker + ' (' + $t.Cik + ')')
    Say ''

    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-WebRequest -Uri $uri -Method Get -TimeoutSec 180 -UseBasicParsing -Headers $headers
        $sw.Stop()
        Say ('- status  : ' + [string]$r.StatusCode)
        Say ('- bytes   : ' + [string]$r.RawContentLength)
        Say ('- seconds : ' + [string][Math]::Round($sw.Elapsed.TotalSeconds, 1))
    }
    catch {
        $msg = $_.Exception.Message
        $status = ''
        if ($_.Exception.PSObject.Properties.Name -contains 'Response' -and $null -ne $_.Exception.Response) {
            $status = [string][int]$_.Exception.Response.StatusCode
        }
        Say ('- FAILED  : ' + $msg)
        if (-not [string]::IsNullOrWhiteSpace($status)) { Say ('- status  : ' + $status) }
    }

    Save-Log
    Start-Sleep -Seconds 2
}

Say ''
Say ('Written: ' + $log)
Save-Log
