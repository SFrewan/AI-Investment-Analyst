#requires -Version 5.1
<#
    WHY THE SEC INGESTION IS BEING REFUSED.

    The backfill reports "Refused: ingestion.policy-permitted@1" for all twenty companies, which
    names the gateway's rule and not the policy decision underneath it. This reads the decision.

    SELECT only; the session is read-only at the server. Makes no network request and no API call.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log = Join-Path $out 'sec-payload.md'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }

Say '# What the SEC payloads actually contain'
Say ''
Say ('Generated ' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z. Read-only.')
Say ''

# ---- database ---------------------------------------------------------------

$psql = $null
$found = @(Get-Command psql -CommandType Application -ErrorAction SilentlyContinue)
if ($found.Count -gt 0) { $psql = [string]$found[0].Source }
if ([string]::IsNullOrWhiteSpace($psql)) {
    $c = @(Get-ChildItem -Path 'C:\Program Files\PostgreSQL' -Filter 'psql.exe' -Recurse -ErrorAction SilentlyContinue)
    if ($c.Count -gt 0) { $psql = [string]$c[0].FullName }
}

$H = 'localhost'; $P = '5432'; $D = ''; $U = ''; $haveDb = $false

if (-not [string]::IsNullOrWhiteSpace($psql)) {
    $cs = $null
    try {
        $text = (& dotnet user-secrets list --project (Join-Path $root 'src\AI.Investment.Api') 2>&1) -join "`n"
        if ($LASTEXITCODE -eq 0) {
            foreach ($line in @($text -split "`n")) {
                $i = $line.IndexOf(' = ')
                if ($i -gt 0 -and $line.Substring(0, $i).Trim() -eq 'Database:ConnectionString') { $cs = $line.Substring($i + 3) }
            }
        }
        $text = $null
    }
    catch { }

    if (-not [string]::IsNullOrWhiteSpace($cs)) {
        $parts = @{}
        foreach ($seg in @($cs -split ';')) {
            $j = $seg.IndexOf('=')
            if ($j -gt 0) { $parts[$seg.Substring(0, $j).Trim().ToLowerInvariant()] = $seg.Substring($j + 1).Trim() }
        }
        $cs = $null
        if ($parts.ContainsKey('host')) { $H = $parts['host'] }
        if ($parts.ContainsKey('port')) { $P = $parts['port'] }
        if ($parts.ContainsKey('database')) { $D = $parts['database'] }
        if ($parts.ContainsKey('username')) { $U = $parts['username'] }
        if ($parts.ContainsKey('password')) { $env:PGPASSWORD = $parts['password'] }
        $parts = $null
        $env:PGOPTIONS = '-c default_transaction_read_only=on'
        $haveDb = $true
    }
}

function Sql([string]$sql) {
    if (-not $script:haveDb) { return 'NO DATABASE' }
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $raw = $null; $code = 0
    try { $raw = & $script:psql -h $script:H -p $script:P -U $script:U -d $script:D -t -A -F ' | ' -c $sql 2>&1; $code = $LASTEXITCODE }
    catch { return 'QUERY FAILED' }
    finally { $ErrorActionPreference = $previous }
    if ($code -ne 0) { return ('QUERY FAILED: ' + (($raw | Out-String).Trim())) }
    return (($raw | Out-String).Trim())
}

function Show([string]$title, [string]$sql) {
    Say ''
    Say ('### ' + $title)
    Say ''
    Say '```'
    $t = Sql $sql
    if ([string]::IsNullOrWhiteSpace($t)) { Say '(no rows)' } else {
        foreach ($r in @($t -split "`n")) { if (-not [string]::IsNullOrWhiteSpace($r)) { Say $r.Trim() } }
    }
    Say '```'
    Save-Log
}

Say '## The newest archived payload'
Say ''

$archive = Join-Path $root 'tests\AI.Investment.Api.Tests\bin\Release\net8.0\archive'

if (-not (Test-Path -Path $archive)) {
    Say 'No archive directory.'
}
else {
    $newest = @(Get-ChildItem -Path $archive -Filter '*.bin' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)

    if ($newest.Count -eq 0) {
        Say 'No archived payloads.'
    }
    else {
        $file = $newest[0]
        Say ('- file    : ' + $file.Name)
        Say ('- bytes   : ' + [string]$file.Length)
        Say ('- written : ' + $file.LastWriteTimeUtc.ToString('yyyy-MM-dd HH:mm:ss') + 'Z')

        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        $isGzip = ($bytes.Length -gt 2 -and $bytes[0] -eq 0x1f -and $bytes[1] -eq 0x8b)
        Say ('- gzip    : ' + [string]$isGzip)

        $take = [Math]::Min(300, $bytes.Length)
        $head = [System.Text.Encoding]::UTF8.GetString($bytes, 0, $take)
        Say ''
        Say 'First 300 bytes:'
        Say '```'
        Say $head
        Say '```'

        try {
            $text = [System.Text.Encoding]::UTF8.GetString($bytes)
            $doc = ConvertFrom-Json $text
            Say ''
            Say ('- parses as JSON : yes')
            $top = @($doc.PSObject.Properties | ForEach-Object { $_.Name })
            Say ('- top-level keys : ' + ($top -join ', '))

            if ($doc.PSObject.Properties.Name -contains 'facts') {
                $tax = @($doc.facts.PSObject.Properties | ForEach-Object { $_.Name })
                Say ('- taxonomies     : ' + ($tax -join ', '))

                if ($tax -contains 'us-gaap') {
                    $tags = @($doc.facts.'us-gaap'.PSObject.Properties | ForEach-Object { $_.Name })
                    Say ('- us-gaap tags   : ' + [string]$tags.Count)
                    foreach ($wanted in @('Revenues', 'Assets', 'NetIncomeLoss', 'AssetsCurrent', 'StockholdersEquity')) {
                        if ($tags -contains $wanted) {
                            $units = @($doc.facts.'us-gaap'.$wanted.units.PSObject.Properties | ForEach-Object { $_.Name })
                            Say ('  - ' + $wanted + ' units: ' + ($units -join ', '))
                        }
                        else {
                            Say ('  - ' + $wanted + ': ABSENT')
                        }
                    }
                }
            }
        }
        catch {
            Say ''
            Say ('- parses as JSON : NO  (' + $_.Exception.Message + ')')
        }
    }
}

Save-Log

Show 'quarantined payloads, newest first' (
    "select quarantined_at_utc, source_id, category, rule_id, reason " +
    'from quarantined_payloads order by quarantined_at_utc desc limit 15')

Show 'SEC ingestion runs today' (
    "select outcome, count(*) as runs, sum(observations_recorded) as observations " +
    "from ingestion_runs where started_at_utc > now() - interval '1 day' " +
    'group by 1 order by 1')

Say ''
Say ('Written: ' + $log)
Save-Log
