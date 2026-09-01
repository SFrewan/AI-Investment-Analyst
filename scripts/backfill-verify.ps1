#requires -Version 5.1
<#
    BLOCK 2B - READ-ONLY VERIFICATION OF WHAT THE BACKFILL ACTUALLY LANDED.

    Answers the three questions the backfill report raises and does not settle:
      1. AAPL prices came back "Refused: ingestion.policy-permitted@1" - denied, or a duplicate?
      2. Every symbol reports zero splits. Is the corporate-actions feed working, or silent?
      3. Every symbol reports 250 sessions where two years is about 500. Where does history start?

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
$log = Join-Path $out 'backfill-verify.md'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }

Say '# Block 2B - what the backfill actually landed'
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

Show 'Q3. price history per instrument: span and depth' (
    "select subject_identifier, count(*) as rows, count(distinct as_of_utc::date) as sessions, " +
    "min(as_of_utc)::date as earliest, max(as_of_utc)::date as latest " +
    "from observations where attribute = 'security.close' " +
    'group by 1 order by 1')

Show 'Q3. does anything at all predate one year ago' (
    "select count(*) as rows_older_than_370_days, min(as_of_utc)::date as oldest " +
    "from observations where attribute = 'security.close' " +
    "and as_of_utc < now() - interval '370 days'")

Show 'Q2. split observations, by attribute' (
    "select attribute, count(*) as rows, count(distinct subject_identifier) as subjects " +
    'from observations group by 1 order by 1')

Show 'Q2. corporate-action ingestion runs: did the calls happen and what came back' (
    "select source_id, category, outcome, count(*) as runs, " +
    'min(started_at_utc)::date as first, max(started_at_utc)::date as last ' +
    'from ingestion_runs group by 1,2,3 order by 1,2,3')

Show 'Q2. archived split payloads (a re-normalise could read these without a new fetch)' (
    "select source_id, count(*) as runs, sum(coalesce(jsonb_array_length(artifacts),0)) as artifacts " +
    "from ingestion_runs where source_id = 'eodhd-splits' group by 1")

Show 'Q1. AAPL ingestion runs, newest first' (
    "select started_at_utc, source_id, category, outcome, " +
    'coalesce(refusal_rule_id, ' + "''" + ') as refusal_rule ' +
    "from ingestion_runs where subject_identifier = 'AAPL.US' " +
    'order by started_at_utc desc limit 12')

Show 'Q1. completed ingestion runs per instrument and category' (
    "select subject_identifier, category, count(*) filter (where outcome = 'Succeeded') as succeeded, " +
    "count(*) filter (where outcome <> 'Succeeded') as other " +
    'from ingestion_runs group by 1,2 order by 1,2')

Show 'Q1. was the AAPL price fetch suppressed as a duplicate rather than denied' (
    "select idempotency_key, count(*) as times_seen, min(claimed_at_utc) as first_claimed " +
    "from processed_actions where idempotency_key like '%AAPL%' " +
    'group by 1 order by 1 limit 10')

Show 'quarantined payloads (a normaliser that refused what it was given)' (
    'select source_id, rule_id, count(*) from quarantined_payloads group by 1,2 order by 3 desc limit 10')

Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
Remove-Item Env:\PGOPTIONS -ErrorAction SilentlyContinue

Say ''
Say '# END'
Save-Log
Write-Host ''
Write-Host ('Written: ' + $log)
exit 0
