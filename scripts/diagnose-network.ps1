#requires -Version 5.1
<#
    READ-ONLY NETWORK DIAGNOSIS.

    The Block 2B pre-flight refused to run because 'eodhd.com' would not resolve. This works out
    whether that is the whole network, this machine's DNS, or that one name - which decides whether
    the backfill is waiting on a retry or on a fix.

    Makes no EODHD API request. Sends no token. Changes nothing.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log = Join-Path $out 'network-diagnosis.txt'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }
function Stamp { return ((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z') }

Say '==============================================================='
Say ' NETWORK DIAGNOSIS - read only, no API request'
Say (' at : ' + (Stamp))
Say '==============================================================='

# ---- 1. what this machine thinks its DNS servers are -----------------------

Say ''
Say '--- configured DNS servers'

try {
    $servers = @(Get-DnsClientServerAddress -AddressFamily IPv4 -ErrorAction Stop |
        Where-Object { @($_.ServerAddresses).Count -gt 0 })

    if ($servers.Count -eq 0) {
        Say '  NONE configured on any IPv4 interface. That alone explains the failure.'
    }

    foreach ($s in $servers) {
        Say ('  ' + $s.InterfaceAlias + ' -> ' + (@($s.ServerAddresses) -join ', '))
    }
}
catch {
    Say ('  could not read: ' + $_.Exception.Message)
}

# ---- 2. does anything resolve at all ---------------------------------------

Say ''
Say '--- name resolution'

$names = @('eodhd.com', 'www.microsoft.com', 'api.nuget.org', 'cloudflare.com')

foreach ($name in $names) {
    try {
        $answers = @(Resolve-DnsName -Name $name -Type A -ErrorAction Stop |
            Where-Object { $_.PSObject.Properties.Name -contains 'IPAddress' })

        if ($answers.Count -eq 0) {
            Say ('  ' + $name.PadRight(20) + ' resolved, but returned no A record')
        }
        else {
            Say ('  ' + $name.PadRight(20) + ' OK  -> ' + (@($answers | ForEach-Object { $_.IPAddress }) -join ', '))
        }
    }
    catch {
        Say ('  ' + $name.PadRight(20) + ' FAILED: ' + $_.Exception.Message)
    }
}

# ---- 3. is it DNS, or is it the wire ---------------------------------------

Say ''
Say '--- direct reachability, bypassing this machine''s resolver'

# 1.1.1.1 is a literal address, so a success here proves packets leave the machine even when
# no name resolves. That is the difference between "DNS is broken" and "there is no network".
try {
    $ping = Test-Connection -ComputerName '1.1.1.1' -Count 2 -Quiet -ErrorAction Stop

    if ($ping) {
        Say '  1.1.1.1 answers ICMP - packets are leaving the machine'
    }
    else {
        Say '  1.1.1.1 does not answer ICMP (may simply be filtered)'
    }
}
catch {
    Say ('  ICMP probe failed: ' + $_.Exception.Message)
}

try {
    $tcp = Test-NetConnection -ComputerName '1.1.1.1' -Port 443 -WarningAction SilentlyContinue

    Say ('  TCP 1.1.1.1:443 -> ' + [string]$tcp.TcpTestSucceeded)
}
catch {
    Say ('  TCP probe failed: ' + $_.Exception.Message)
}

# ---- 4. resolve the vendor through a public resolver -----------------------

Say ''
Say '--- eodhd.com through 1.1.1.1 directly'

try {
    $direct = @(Resolve-DnsName -Name 'eodhd.com' -Type A -Server '1.1.1.1' -ErrorAction Stop |
        Where-Object { $_.PSObject.Properties.Name -contains 'IPAddress' })

    if ($direct.Count -gt 0) {
        Say ('  OK -> ' + (@($direct | ForEach-Object { $_.IPAddress }) -join ', '))
        Say '  The name is fine; this machine''s own resolver is what failed.'
    }
    else {
        Say '  no A record returned'
    }
}
catch {
    Say ('  FAILED: ' + $_.Exception.Message)
}

# ---- 5. the actual pre-flight, repeated ------------------------------------

Say ''
Say '--- https HEAD to eodhd.com (no token, no query string)'

try {
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    $probe = Invoke-WebRequest -Uri 'https://eodhd.com' -Method Head -TimeoutSec 20 -UseBasicParsing
    Say ('  OK, status ' + [string]$probe.StatusCode)
}
catch {
    Say ('  FAILED: ' + $_.Exception.Message)
}

Say ''
Say ('  finished: ' + (Stamp))
Save-Log

Write-Host ''
Write-Host ('Written: ' + $log)
exit 0
