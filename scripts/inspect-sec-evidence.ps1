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
$log = Join-Path $out 'sec-evidence.md'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }

Say '# SEC fundamentals - evidence now available'
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

Say '## Evidence'

Show 'read-only session proof' 'show default_transaction_read_only'

Show 'audit event types today' (
    'select event_type, capability, count(*) as records from audit_records ' +
    "where occurred_at_utc >= date_trunc('day', now() at time zone 'UTC') at time zone 'UTC' " +
    'group by 1, 2 order by 3 desc limit 40')

Show 'what the daily ceiling actually counts (ActionExecuted since midnight UTC)' (
    'select capability, count(*) as actions_today from audit_records ' +
    "where event_type = 'ActionExecuted' " +
    "and occurred_at_utc >= date_trunc('day', now() at time zone 'UTC') at time zone 'UTC' " +
    'group by 1 order by 2 desc')

Show 'distinct point-in-time series now held' (
    'select count(*) as company_attribute_series from (' +
    'select subject_identifier, attribute from observations ' +
    "where attribute like 'financials.%' group by 1, 2) s")

Show 'restatement chains (same company, attribute and period, published more than once)' (
    'select count(*) as chains, sum(publications) as publications from (' +
    'select subject_identifier, attribute, as_of_utc, count(*) as publications ' +
    "from observations where attribute like 'financials.%' " +
    'group by 1, 2, 3 having count(*) > 1) c')

Show 'evidence usable at the start of the price window (published on or before 2025-09-01)' (
    'select count(*) as figures, count(distinct subject_identifier) as companies ' +
    "from observations where attribute like 'financials.%' " +
    "and published_at_utc <= timestamptz '2025-09-01 00:00:00+00'")

Show 'new information arriving inside the price window (2025-09-01 to 2026-08-31)' (
    'select count(*) as figures, count(distinct subject_identifier) as companies ' +
    "from observations where attribute like 'financials.%' " +
    "and published_at_utc > timestamptz '2025-09-01 00:00:00+00' " +
    "and published_at_utc <= timestamptz '2026-08-31 00:00:00+00'")

Show 'publications per month inside the price window' (
    "select to_char(published_at_utc, 'YYYY-MM') as month, count(*) as figures, " +
    'count(distinct subject_identifier) as companies ' +
    "from observations where attribute like 'financials.%' " +
    "and published_at_utc > timestamptz '2025-09-01 00:00:00+00' " +
    "and published_at_utc <= timestamptz '2026-08-31 00:00:00+00' " +
    'group by 1 order by 1')

Show 'subject kinds and attribute families held' (
    'select kind, count(*) as observations, count(distinct subject_identifier) as subjects, ' +
    'count(distinct attribute) as attributes from (' +
    "select case when attribute like 'financials.%' then 'fundamentals' else 'other' end as kind, " +
    'subject_identifier, attribute from observations) o group by 1 order by 2 desc')

Show 'price observations held, for comparison' (
    'select count(*) as price_observations, count(distinct subject_identifier) as instruments ' +
    "from observations where attribute not like 'financials.%'")

Say ''
Say ('Written: ' + $log)
Save-Log
