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
$log = Join-Path $out 'price-depth.md'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }

Say '# Price depth - what is actually held'
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

Show 'tables in this database' (
    "select table_name from information_schema.tables where table_schema = 'public' " +
    'order by 1')

Show 'price coverage per instrument' (
    'select subject_identifier, count(*) as rows, count(distinct as_of_utc) as sessions, ' +
    'min(as_of_utc) as earliest, max(as_of_utc) as latest ' +
    "from observations where attribute not like 'financials.%' " +
    'group by 1 order by 2 desc')

Show 'sessions that carry more than one publication (a restatement, not a duplicate)' (
    'select subject_identifier, count(*) as sessions_with_multiple from (' +
    'select subject_identifier, as_of_utc, count(*) as publications ' +
    "from observations where attribute not like 'financials.%' " +
    'group by 1, 2 having count(*) > 1) d group by 1 order by 2 desc')

Show 'distinct publication times behind the price series' (
    'select subject_identifier, count(distinct published_at_utc) as publication_times, ' +
    'count(distinct retrieved_at_utc) as retrievals ' +
    "from observations where attribute not like 'financials.%' " +
    'group by 1 order by 2 desc limit 8')

Show 'anything at all older than the frozen window start' (
    'select count(*) as rows_before_2025_09_02 from observations ' +
    "where attribute not like 'financials.%' " +
    "and as_of_utc < timestamptz '2025-09-02 00:00:00+00'")

Show 'archived raw payloads by source (a re-normalise could read these without a fetch)' (
    'select source_id, category, count(*) as artefacts from ingestion_artifacts ' +
    'group by 1, 2 order by 3 desc')

Show 'ingestion runs by source and outcome, all time' (
    'select source_id, category, outcome, count(*) as runs, ' +
    'min(started_at_utc) as first_run, max(started_at_utc) as last_run ' +
    'from ingestion_runs group by 1, 2, 3 order by 1, 2, 4 desc')

Show 'registered sources' 'select source_id, status from data_sources order by 1'

Show 'watches and their instruments' (
    'select count(*) as watches, count(distinct subject_identifier) as subjects from watches')

Say ''
Say ('Written: ' + $log)
Save-Log
