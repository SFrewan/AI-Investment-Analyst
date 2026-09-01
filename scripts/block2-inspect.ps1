#requires -Version 5.1
<#
    BLOCK 2 - READ-ONLY INSPECTION OF THE EVIDENCE BASE.

    What history exists, for which instruments, over what span, and what the universe
    mechanism actually is. SELECT only; the session is read-only at the server.
    Starts nothing, changes nothing, makes no network request.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\audit'
$null = New-Item -ItemType Directory -Force -Path $out
$log = Join-Path $out '70-evidence-base.md'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }

Say '# Block 2 - evidence base, before'
Say ''
Say ('Generated ' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z. Read-only.')
Say ''

# ---- test coverage for the two readers -------------------------------------

Say '## Existing tests for the two readers named in Block 2'
Say ''
Say '```'

foreach ($name in @('LedgerExposureProvider', 'PriceSeriesReader')) {
    $hits = 0
    $files = @(Get-ChildItem -Path (Join-Path $root 'tests') -Recurse -File -Filter '*.cs' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })

    foreach ($file in $files) {
        $found = @(Select-String -Path $file.FullName -Pattern ('\b' + $name + '\b') -ErrorAction SilentlyContinue)
        foreach ($m in $found) {
            $hits++
            Say ('  ' + $file.Name + ':' + [string]$m.LineNumber)
        }
    }

    Say ($name + ' -> ' + [string]$hits + ' references in tests')
    Say ''
}

Say '```'
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

Say '## Current coverage'

Show 'read-only session proof' 'show default_transaction_read_only'

Show 'observations per subject and attribute, with span' (
    'select subject_kind, subject_identifier, attribute, count(*) as rows, ' +
    'min(as_of_utc)::date as earliest, max(as_of_utc)::date as latest, ' +
    'count(distinct as_of_utc::date) as sessions ' +
    'from observations group by 1,2,3 order by rows desc limit 30')

Show 'how far the deepest series reaches (sessions, against the 60 the rule needs)' (
    'select subject_identifier, count(distinct as_of_utc::date) as sessions, ' +
    "case when count(distinct as_of_utc::date) >= 60 then 'ENOUGH' else 'SHORT' end as verdict " +
    "from observations where attribute = 'security.close' " +
    'group by 1 order by sessions desc limit 30')

Show 'watches configured (the universe today)' (
    'select id, name, target_kind, target_identifier, enabled, trigger_type, ' +
    'cycle_template, cooldown, fire_count, last_fired_at_utc from watches order by name')

Show 'registered data sources' (
    'select id, name, source_type, active, confirmation_state from data_sources order by id')

Show 'ingestion runs by source and outcome' (
    'select source_id, outcome, count(*), min(started_at_utc)::date as first, ' +
    'max(started_at_utc)::date as last from ingestion_runs group by 1,2 order by 1,2')

Show 'archived payloads (what a re-normalise could read without a new fetch)' (
    'select source_id, count(*) as runs, sum(coalesce(array_length(artifacts,1),0)) as artifacts ' +
    'from ingestion_runs group by 1 order by 1')

Show 'distinct trading dates present, newest first' (
    "select as_of_utc::date as session, count(*) as observations from observations " +
    "where attribute = 'security.close' group by 1 order by 1 desc limit 15")

Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
Remove-Item Env:\PGOPTIONS -ErrorAction SilentlyContinue

Say ''
Say '# END'
Save-Log
Write-Host ''
Write-Host ('Written: ' + $log)
exit 0
