#requires -Version 5.1
<#
    READ-ONLY FORENSIC STATUS CHECK.

    Investigation only. It starts nothing, changes nothing, and makes no network request.

    THE DATABASE SESSION IS READ-ONLY AT THE SERVER.
    PGOPTIONS sets default_transaction_read_only=on for every psql session below, so an
    INSERT/UPDATE/DELETE/DDL would be refused by PostgreSQL itself even if one were
    written by mistake. Every statement here is a SELECT regardless.

    It does NOT start the API, does NOT touch OperationsHost:RunCycles, does NOT start a
    cycle, and makes NO EODHD request.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log  = Join-Path $out 'forensic-status.txt'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }

$WatchId = '55dbe3e7-71ed-4602-bb34-d1ab41b3e3d0'
$CycleId = 'a2c5bc33-40a9-4d7e-8252-56cabbedc691'

Say '=== READ-ONLY FORENSIC STATUS ==='
Say ('UTC now : ' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
Say ('local   : ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
Say ('repo    : ' + $root)
Say ''
Say '  Nothing is started by this script. The database session is READ ONLY at the server'
Say '  (default_transaction_read_only=on), so a write would be refused by PostgreSQL.'
Save-Log

# ------------------------------------------------------------------------------------------
# 1. Is anything running?
# ------------------------------------------------------------------------------------------
Say ''
Say '--- 1. RUNNING PROCESSES ---'

$apiProcs = @(Get-Process -Name 'AI.Investment.Api' -ErrorAction SilentlyContinue)
Say ('  AI.Investment.Api processes : ' + $apiProcs.Count)
foreach ($p in $apiProcs) {
    Say ('      pid ' + $p.Id + '  started ' + $p.StartTime.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
}

$dotnetProcs = @(Get-Process -Name 'dotnet' -ErrorAction SilentlyContinue)
Say ('  dotnet processes            : ' + $dotnetProcs.Count)

$listening = @()
try { $listening = @(Get-NetTCPConnection -LocalPort 5143 -State Listen -ErrorAction SilentlyContinue) } catch { }
Say ('  listening on 5143           : ' + ($listening.Count -gt 0))

$pg = @(Get-Service -Name 'postgresql*' -ErrorAction SilentlyContinue)
if ($pg.Count -eq 0) { Say '  PostgreSQL service          : not found by name' }
foreach ($s in $pg) { Say ('  PostgreSQL service          : ' + $s.Name + ' = ' + $s.Status) }
Save-Log

# ------------------------------------------------------------------------------------------
# 2. When did the machine go down and come back?
# ------------------------------------------------------------------------------------------
Say ''
Say '--- 2. SHUTDOWN / BOOT EVIDENCE ---'
try {
    $boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
    Say ('  last boot (UTC) : ' + $boot.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
}
catch { Say ('  last boot : unavailable (' + $_.Exception.Message + ')') }

try {
    $evts = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 6005, 6006, 6008, 41, 1074 } -MaxEvents 12 -ErrorAction SilentlyContinue)
    if ($evts.Count -eq 0) { Say '  no shutdown/boot events readable' }
    foreach ($e in $evts) {
        Say ('    ' + $e.TimeCreated.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z  id=' + $e.Id + '  ' + ($e.Message -split "`n")[0].Trim())
    }
}
catch { Say ('  event log unavailable: ' + $_.Exception.Message) }
Save-Log

# ------------------------------------------------------------------------------------------
# 3. psql, read-only.
# ------------------------------------------------------------------------------------------
Say ''
Say '--- 3. DATABASE ---'

$psql = $null
$found = @(Get-Command psql -CommandType Application -ErrorAction SilentlyContinue)
if ($found.Count -gt 0) { $psql = [string]$found[0].Source }
if ([string]::IsNullOrWhiteSpace($psql)) {
    $c = @(Get-ChildItem -Path 'C:\Program Files\PostgreSQL' -Filter 'psql.exe' -Recurse -ErrorAction SilentlyContinue)
    if ($c.Count -gt 0) { $psql = [string]$c[0].FullName }
}

$H = 'localhost'; $P = '5432'; $D = ''; $U = ''
$haveDb = $false

if ([string]::IsNullOrWhiteSpace($psql)) {
    Say '  psql not found. No database evidence available.'
}
else {
    Say ('  psql : ' + $psql)

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

    if ([string]::IsNullOrWhiteSpace($cs)) {
        Say '  Database:ConnectionString unavailable. No database evidence.'
    }
    else {
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
        Say ('  target : ' + $D + ' on ' + $H + ':' + $P + '   (READ ONLY session)')
    }
}

# The server refuses writes for every session below.
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

if ($haveDb) {
    Show 'server / database / clock' 'select version(), current_database(), now()'
    Show 'read-only session proof (must be on)' 'show default_transaction_read_only'

    Show 'THE WATCH (id | enabled | fire_count | last_fired | cooldown | interval | disabled_reason)' (
        "select id, enabled, fire_count, coalesce(last_fired_at_utc::text,'(never)'), " +
        "cooldown::text, condition_interval::text, coalesce(disabled_reason,'-') " +
        "from watches where id = '$WatchId'")

    Show 'COOLDOWN MATH (last_fired | expires | now | elapsed? | seconds_remaining)' (
        "select last_fired_at_utc, last_fired_at_utc + cooldown, now(), " +
        "(now() >= last_fired_at_utc + cooldown) as cooldown_elapsed, " +
        "greatest(0, ceil(extract(epoch from (last_fired_at_utc + cooldown) - now())))::bigint " +
        "from watches where id = '$WatchId'")

    Show 'ALL OPERATING CYCLES (id | status | stage | started | stopped | reason)' (
        "select id, status, stage, started_at_utc, coalesce(stopped_at_utc::text,'-'), " +
        "coalesce(stopped_reason,'-') from operating_cycles order by started_at_utc")

    Show 'CYCLE COUNT' 'select count(*) from operating_cycles'

    Show 'THE FIRST CYCLE, IN FULL (trigger_key | watch_id | correlation | capability | template)' (
        "select trigger_key, coalesce(watch_id::text,'-'), correlation_id, capability, template " +
        "from operating_cycles where id = '$CycleId'")

    Show 'DUPLICATE trigger_key (must be no rows)' (
        'select trigger_key, count(*) from operating_cycles group by trigger_key having count(*) > 1')

    Show 'ALL INGESTION RUNS (id | source | outcome | reason | subject | started | completed)' (
        "select id, source_id, outcome, coalesce(reason,'-'), subject_identifier, " +
        "started_at_utc, coalesce(completed_at_utc::text,'-') from ingestion_runs order by started_at_utc")

    Show 'INGESTION RUN COUNT (total | eodhd-eod)' (
        "select (select count(*) from ingestion_runs), " +
        "(select count(*) from ingestion_runs where source_id = 'eodhd-eod')")

    Show 'OWNED ROWS OF THE NEWEST RUN (source | category | region | subject_kind | subject_id | correlation)' (
        "select source_id, category, region, subject_kind, coalesce(subject_identifier,'(null)'), " +
        "correlation_id from ingestion_runs order by started_at_utc desc limit 1")

    Show 'ACTION EXECUTIONS BY TYPE (action_type | count | newest)' (
        'select action_type, count(*), max(started_at_utc) from action_executions ' +
        'group by action_type order by max(started_at_utc) desc')

    Show 'ingestion.fetch EXECUTIONS (started | completed | status)' (
        "select started_at_utc, coalesce(completed_at_utc::text,'-'), status from action_executions " +
        "where action_type = 'ingestion.fetch' order by started_at_utc desc limit 10")

    Show 'AUDIT RECORDS BY TYPE (action_type | count | newest)' (
        'select action_type, count(*), max(occurred_at_utc) from audit_records ' +
        'group by action_type order by max(occurred_at_utc) desc limit 20')

    Show 'NEWEST 15 AUDIT RECORDS (occurred | action_type | outcome | summary)' (
        'select occurred_at_utc, action_type, outcome, left(summary, 70) from audit_records ' +
        'order by occurred_at_utc desc limit 15')

    Show 'OBSERVATIONS / ESCALATIONS / QUARANTINE COUNTS' (
        'select (select count(*) from observations), (select count(*) from escalations), ' +
        '(select count(*) from quarantined_payloads)')

    Show 'ESCALATIONS (raised | reason | cycle)' (
        "select raised_at_utc, left(reason, 70), coalesce(cycle_id::text,'-') " +
        "from escalations order by raised_at_utc desc limit 10")

    Show 'NEWEST WRITE ANYWHERE (latest timestamp across the main tables)' (
        "select 'operating_cycles' as t, max(started_at_utc)::text from operating_cycles " +
        "union all select 'ingestion_runs', max(started_at_utc)::text from ingestion_runs " +
        "union all select 'audit_records', max(occurred_at_utc)::text from audit_records " +
        "union all select 'action_executions', max(started_at_utc)::text from action_executions " +
        "union all select 'observations', max(retrieved_at_utc)::text from observations")
}
Save-Log

# ------------------------------------------------------------------------------------------
# 4. The API log the observation run would have produced.
# ------------------------------------------------------------------------------------------
Say ''
Say '--- 4. LIVE RUN ARTIFACTS ---'
foreach ($name in @('live-observation.txt', 'live-api.log', 'live-api.err.log')) {
    $p = Join-Path $out $name
    if (Test-Path $p) {
        $f = Get-Item $p
        Say ('  PRESENT : ' + $name + '  ' + $f.Length + ' bytes  last write ' +
             $f.LastWriteTimeUtc.ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
    }
    else {
        Say ('  ABSENT  : ' + $name)
    }
}

Say ''
Say '  The five newest files anywhere under artifacts\, by write time:'
foreach ($f in @(Get-ChildItem -Path (Join-Path $root 'artifacts') -Recurse -File -ErrorAction SilentlyContinue |
                 Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 5)) {
    Say ('      ' + $f.LastWriteTimeUtc.ToString('yyyy-MM-dd HH:mm:ss') + 'Z  ' + $f.Name)
}

# ------------------------------------------------------------------------------------------
# 5. Where the two identifiers appear on disk.
# ------------------------------------------------------------------------------------------
Say ''
Say '--- 5. IDENTIFIER SEARCH ---'
foreach ($id in @($WatchId, $CycleId)) {
    Say ''
    Say ('  ' + $id)
    $hits = @(Get-ChildItem -Path $root -Recurse -File -Include *.txt,*.log,*.md,*.json,*.cs,*.ps1,*.cmd,*.sql `
                -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git|StrykerOutput)\\' } |
              Select-String -Pattern $id -SimpleMatch -ErrorAction SilentlyContinue)
    if ($hits.Count -eq 0) { Say '      (not found on disk)' }
    foreach ($h in @($hits | Select-Object -First 12)) {
        Say ('      ' + $h.Path.Replace($root, '.') + ':' + $h.LineNumber)
    }
    if ($hits.Count -gt 12) { Say ('      ... and ' + ($hits.Count - 12) + ' more') }
}

# ------------------------------------------------------------------------------------------
# 6. Any UnauthorizedWriteException left on disk, anywhere.
# ------------------------------------------------------------------------------------------
Say ''
Say '--- 6. UnauthorizedWriteException ON DISK ---'
$uw = @(Get-ChildItem -Path (Join-Path $root 'artifacts') -Recurse -File -ErrorAction SilentlyContinue |
        Select-String -Pattern 'UnauthorizedWriteException' -SimpleMatch -ErrorAction SilentlyContinue)
if ($uw.Count -eq 0) { Say '  none found under artifacts\' }
foreach ($h in @($uw | Select-Object -First 10)) {
    Say ('  ' + $h.Path.Replace($root, '.') + ':' + $h.LineNumber)
}

# ------------------------------------------------------------------------------------------
# 7. Working tree, so the code state is not in doubt.
# ------------------------------------------------------------------------------------------
Say ''
Say '--- 7. WORKING TREE ---'
$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    foreach ($l in @(& git -C $root status --short 2>&1)) { Say ('  ' + [string]$l) }
    Say ''
    foreach ($l in @(& git -C $root diff --stat 2>&1 | Select-Object -Last 6)) { Say ('  ' + [string]$l) }
}
catch { Say '  git unavailable' }
finally { $ErrorActionPreference = $previous }

Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
Remove-Item Env:\PGOPTIONS -ErrorAction SilentlyContinue

Say ''
Say '=== END ==='
Say '  Read-only. Nothing was started, changed, migrated or requested.'

Save-Log
Write-Host ''
Write-Host ('Written: ' + $log)
exit 0
