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
$log = Join-Path $out 'sec-refusal.md'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }

Say '# Why the SEC ingestion is refused'
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

Show 'SEC ingestion runs, newest first' (
    "select started_at_utc, request_subject_identifier, outcome, refusal_rule_id, reason " +
    "from ingestion_runs where request_source_id = 'sec-edgar' " +
    'order by started_at_utc desc limit 25')

Show 'audit records for ingestion.fetch today, newest first' (
    "select occurred_at_utc, event_type, outcome, reason " +
    "from audit_records where action_type = 'ingestion.fetch' " +
    "and occurred_at_utc > now() - interval '1 day' " +
    'order by occurred_at_utc desc limit 25')

Show 'every distinct denial reason recorded today' (
    "select outcome, reason, count(*) as times " +
    "from audit_records where occurred_at_utc > now() - interval '1 day' " +
    "and outcome not in ('Executed') " +
    'group by 1, 2 order by 3 desc limit 25')

Show 'action executions per capability today' (
    "select capability, count(*) as actions " +
    "from action_executions where started_at_utc > now() - interval '1 day' " +
    'group by 1 order by 2 desc')

Show 'fundamental observations already stored' (
    "select attribute, count(*) as rows, count(distinct subject_identifier) as subjects " +
    "from observations where attribute like 'financials.%' " +
    'group by 1 order by 1')

Say ''
Say ('Written: ' + $log)
Save-Log
