#requires -Version 5.1
<#
    READ-ONLY DIAGNOSIS OF THE DuplicateSuppressed INGESTION.

    The post-fix cycle's ingestion was refused as a duplicate. This asks the database who
    claimed that idempotency key and when. SELECT only; the session is read-only at the
    server. Starts nothing, changes nothing, makes no network request.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log  = Join-Path $out 'diagnose-duplicate.txt'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }

$Key = '4020cecc6458e5f92cedc1dc7e161c845ef00b7ba7c973fe73dec0147f68ab3e'

$psql = $null
$found = @(Get-Command psql -CommandType Application -ErrorAction SilentlyContinue)
if ($found.Count -gt 0) { $psql = [string]$found[0].Source }
if ([string]::IsNullOrWhiteSpace($psql)) {
    $c = @(Get-ChildItem -Path 'C:\Program Files\PostgreSQL' -Filter 'psql.exe' -Recurse -ErrorAction SilentlyContinue)
    if ($c.Count -gt 0) { $psql = [string]$c[0].FullName }
}

$H = 'localhost'; $P = '5432'; $D = ''; $U = ''
$haveDb = $false

Say '=== WHY THE POST-FIX INGESTION WAS SUPPRESSED AS A DUPLICATE ==='
Say ('UTC now : ' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
Say '  READ ONLY. SELECT statements only, refused at the server if not.'
Save-Log

if ([string]::IsNullOrWhiteSpace($psql)) { Say '  psql not found.'; Save-Log; exit 1 }

$apiProject = Join-Path $root 'src\AI.Investment.Api'
$cs = $null; $text = $null
try {
    $text = (& dotnet user-secrets list --project $apiProject 2>&1) -join "`n"
    if ($LASTEXITCODE -eq 0) {
        foreach ($line in @($text -split "`n")) {
            $i = $line.IndexOf(' = ')
            if ($i -gt 0 -and $line.Substring(0, $i).Trim() -eq 'Database:ConnectionString') { $cs = $line.Substring($i + 3) }
        }
    }
}
catch { }
finally { $text = $null }

if ([string]::IsNullOrWhiteSpace($cs)) { Say '  connection string unavailable.'; Save-Log; exit 1 }

$parts = @{}
foreach ($seg in @($cs -split ';')) {
    $j = $seg.IndexOf('=')
    if ($j -gt 0) { $parts[$seg.Substring(0, $j).Trim().ToLowerInvariant()] = $seg.Substring($j + 1).Trim() }
}
$cs = $null
if ($parts.ContainsKey('host'))     { $H = $parts['host'] }
if ($parts.ContainsKey('port'))     { $P = $parts['port'] }
if ($parts.ContainsKey('database')) { $D = $parts['database'] }
if ($parts.ContainsKey('username')) { $U = $parts['username'] }
if ($parts.ContainsKey('password')) { $env:PGPASSWORD = $parts['password'] }
$parts = $null
$haveDb = $true
$env:PGOPTIONS = '-c default_transaction_read_only=on'

function Sql([string]$sql) {
    if (-not $haveDb) { return 'NO DATABASE' }
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $raw = $null; $code = 0
    try { $raw = & $psql -h $H -p $P -U $U -d $D -t -A -F ' | ' -c $sql 2>&1; $code = $LASTEXITCODE }
    catch { return 'QUERY FAILED' }
    finally { $ErrorActionPreference = $previous }
    if ($code -ne 0) { return ('QUERY FAILED: ' + (($raw | Out-String).Trim())) }
    return (($raw | Out-String).Trim())
}

function Show([string]$title, [string]$sql) {
    Say ''
    Say ('  ' + $title)
    $t = Sql $sql
    if ([string]::IsNullOrWhiteSpace($t)) { Say '      (no rows)'; return }
    foreach ($r in @($t -split "`n")) {
        if (-not [string]::IsNullOrWhiteSpace($r)) { Say ('      ' + $r.Trim()) }
    }
}

Show 'read-only session proof' 'show default_transaction_read_only'

Show 'processed_actions columns' (
    "select column_name, data_type from information_schema.columns " +
    "where table_name = 'processed_actions' order by ordinal_position")

Show 'THE CLAIM ON THIS KEY (the whole row)' (
    "select * from processed_actions where idempotency_key = '$Key'")

Show 'ALL idempotency claims, newest first' (
    'select * from processed_actions order by 1 desc limit 20')

Show 'the pre-fix ingestion.fetch execution that made the claim' (
    "select execution_id, proposal_id, decision_id, status, started_at_utc, completed_at_utc " +
    "from action_executions where action_type = 'ingestion.fetch' order by started_at_utc")

Show 'DuplicateSuppressed audit detail (shows the key and the proposal)' (
    "select occurred_at_utc, left(details::text, 400) from audit_records " +
    "where event_type = 'DuplicateSuppressed' order by occurred_at_utc desc limit 3")

Show 'both ingestion runs / attempts recorded' (
    "select id, source_id, outcome, coalesce(refusal_rule_id,'-'), coalesce(left(reason,60),'-'), " +
    'started_at_utc, request_fingerprint from ingestion_runs order by started_at_utc')

Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
Remove-Item Env:\PGOPTIONS -ErrorAction SilentlyContinue

Say ''
Say '=== END ==='
Save-Log
Write-Host ''
Write-Host ('Written: ' + $log)
exit 0
