#requires -Version 5.1
<#
    POST-FIX LIVE END-TO-END VERIFICATION.

    Starts the API with OperationsHost__RunCycles=true, lets the scheduler fire the AAPL
    watch, follows the cycle through every stage, proves the cooldown then suppresses the
    following ticks, stops the API, and verifies everything from the database.

    NO ARTIFICIAL WAIT. The cooldown elapsed hours ago.

    THE STRANDED PRE-FIX CYCLE IS LEFT ALONE ON PURPOSE.
    EfCycleStore.GetRunnableAsync returns Running cycles whose lease has expired - that is
    the platform's own crash recovery. OperatingCycleRunner.DriveAsync then checks
    CheckBudget first, and Elapsed = now - StartedAtUtc is already far past the 15-minute
    CycleMaxWallClock, so the cycle suspends itself with "budget exhausted" through ordinary
    domain rules. Nothing here edits a row to make that happen.

    Admission was checked before writing this: MaxConcurrentCycles 4 and
    MaxConcurrentCyclesPerCapability 2, so one stranded Running cycle cannot keep the new
    one out.
#>

[CmdletBinding()]
param(
    [ValidateRange(2, 40)]
    [int]$MaxMinutes = 14,

    [ValidateRange(1, 20)]
    [int]$RequiredSuppressions = 3
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root  = Split-Path -Parent $PSScriptRoot
$out   = Join-Path $root 'artifacts\verify'
$null  = New-Item -ItemType Directory -Force -Path $out
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$log   = Join-Path $out ('live-verification-' + $stamp + '.txt')
$apiOut = Join-Path $out ('live-api-' + $stamp + '.log')
$apiErr = Join-Path $out ('live-api-' + $stamp + '.err.log')

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }
function Stamp { return (Get-Date).ToUniversalTime().ToString('HH:mm:ss') + 'Z' }

$Symbol   = 'AAPL.US'
$WatchId  = '55dbe3e7-71ed-4602-bb34-d1ab41b3e3d0'
$OldCycle = 'a2c5bc33-40a9-4d7e-8252-56cabbedc691'
$BaseUrl  = 'http://localhost:5143'

$psql = $null
$found = @(Get-Command psql -CommandType Application -ErrorAction SilentlyContinue)
if ($found.Count -gt 0) { $psql = [string]$found[0].Source }
if ([string]::IsNullOrWhiteSpace($psql)) {
    $c = @(Get-ChildItem -Path 'C:\Program Files\PostgreSQL' -Filter 'psql.exe' -Recurse -ErrorAction SilentlyContinue)
    if ($c.Count -gt 0) { $psql = [string]$c[0].FullName }
}

$H = 'localhost'; $P = '5432'; $D = ''; $U = ''
$haveDb = $false

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

$exitCode = 0
$api = $null

Say '=== POST-FIX LIVE END-TO-END VERIFICATION ==='
Say ('UTC now : ' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
Say ('repo    : ' + $root)
Save-Log

try {
    # ----------------------------------------------------------------------------------
    # 0. Preflight.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 0. PREFLIGHT ---'

    if ([string]::IsNullOrWhiteSpace($psql)) { Say '  psql not found. STOPPING.'; Save-Log; exit 1 }
    Say ('  psql : ' + $psql)

    $apiExe = Join-Path $root 'src\AI.Investment.Api\bin\Release\net8.0\AI.Investment.Api.exe'
    if (-not (Test-Path $apiExe)) { Say '  Release binary missing. STOPPING.'; Save-Log; exit 1 }
    Say ('  api  : ' + $apiExe)
    Say ('  built: ' + (Get-Item $apiExe).LastWriteTimeUtc.ToString('yyyy-MM-dd HH:mm:ss') + 'Z')

    $busy = @(Get-NetTCPConnection -LocalPort 5143 -State Listen -ErrorAction SilentlyContinue)
    if ($busy.Count -gt 0) { Say '  Something already listens on 5143. STOPPING.'; Save-Log; exit 1 }
    Say '  port 5143 free : True'

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

    if ([string]::IsNullOrWhiteSpace($cs)) { Say '  Database:ConnectionString unavailable. STOPPING.'; Save-Log; exit 1 }

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
    Say ('  database : ' + $D + ' on ' + $H + ':' + $P)

    # ----------------------------------------------------------------------------------
    # 1. Baseline. Everything new must be provably new.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 1. BASELINE ---'

    $baseCycles  = Sql 'select count(*) from operating_cycles'
    $baseRuns    = Sql 'select count(*) from ingestion_runs'
    $baseAudit   = Sql 'select count(*) from audit_records'
    $baseExec    = Sql 'select count(*) from action_executions'
    $baseObs     = Sql 'select count(*) from observations'
    $baseEsc     = Sql 'select count(*) from escalations'
    $baseOutbox  = Sql 'select count(*) from outbox_messages'
    $baseFire    = Sql "select fire_count from watches where id = '$WatchId'"
    $baseSuppress = Sql "select count(*) from audit_records where event_type = 'WatchSuppressed'"

    Say ('  operating_cycles   : ' + $baseCycles)
    Say ('  ingestion_runs     : ' + $baseRuns)
    Say ('  audit_records      : ' + $baseAudit)
    Say ('  action_executions  : ' + $baseExec)
    Say ('  observations       : ' + $baseObs)
    Say ('  escalations        : ' + $baseEsc)
    Say ('  outbox_messages    : ' + $baseOutbox)
    Say ('  watch fire_count   : ' + $baseFire)
    Say ('  WatchSuppressed    : ' + $baseSuppress)

    Say ''
    Say '  the stranded pre-fix cycle, before:'
    Say ('      ' + (Sql ("select id, status, stage, coalesce(stopped_at_utc::text,'-'), " +
                          "coalesce(stopped_reason,'-') from operating_cycles where id = '$OldCycle'")))
    Say ''
    Say '  It is deliberately NOT touched. The runner recovers it: an expired lease makes it'
    Say '  runnable again, and its wall clock is hours past CycleMaxWallClock, so the domain'
    Say '  suspends it as budget-exhausted. That is a real terminal state reached by real rules.'
    Save-Log

    # ----------------------------------------------------------------------------------
    # 2. Start the API.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 2. START THE API ---'
    Say '  OperationsHost__RunCycles = true   (environment of this one process only)'
    Say '  ASPNETCORE_ENVIRONMENT    = Development'
    Say ('  ASPNETCORE_URLS           = ' + $BaseUrl)

    $env:OperationsHost__RunCycles = 'true'
    $env:ASPNETCORE_ENVIRONMENT    = 'Development'
    $env:ASPNETCORE_URLS           = $BaseUrl

    $api = Start-Process -FilePath $apiExe `
        -WorkingDirectory $apiProject `
        -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr `
        -PassThru -WindowStyle Hidden

    Say ('  pid ' + $api.Id + ' started ' + (Stamp))
    Save-Log

    $healthy = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 2
        try {
            $r = Invoke-WebRequest -Uri ($BaseUrl + '/health') -UseBasicParsing -TimeoutSec 5
            if ([int]$r.StatusCode -eq 200) { $healthy = $true; break }
        }
        catch { }
    }

    if (-not $healthy) {
        Say '  /health never answered. Reporting stderr and stopping.'
        foreach ($l in @(Get-Content $apiErr -ErrorAction SilentlyContinue | Select-Object -First 40)) { Say ('    ' + $l) }
        $exitCode = 1
    }
    else {
        Say ('  healthy ' + (Stamp) + '  (startup delay is 30s, cycle interval 30s)')
        Save-Log

        # ------------------------------------------------------------------------------
        # 3. Observe.
        # ------------------------------------------------------------------------------
        Say ''
        Say ('--- 3. OBSERVING (max ' + $MaxMinutes + ' min; stops early once the new cycle is')
        Say ('    terminal and ' + $RequiredSuppressions + ' cooldown suppressions have been recorded) ---')

        $deadline = (Get-Date).AddMinutes($MaxMinutes)
        $last = ''
        $newCycleTerminal = $false
        $suppressions = 0

        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 15

            $snap = Sql ("select (select count(*) from operating_cycles) || ' | ' || " +
                         "(select count(*) from ingestion_runs) || ' | ' || " +
                         "(select fire_count from watches where id = '$WatchId') || ' | ' || " +
                         "(select count(*) from audit_records where event_type = 'WatchSuppressed') || ' | ' || " +
                         "(select count(*) from operating_cycles where stopped_at_utc is null) || ' | ' || " +
                         "(select count(*) from observations)")

            if ($snap -ne $last) {
                Say ('  ' + (Stamp) + '  cycles|runs|fires|suppressed|unstopped|obs = ' + $snap)
                $last = $snap
                Save-Log
            }

            $s = Sql "select count(*) from audit_records where event_type = 'WatchSuppressed'"
            if ($s -notlike 'QUERY FAILED*' -and $baseSuppress -notlike 'QUERY FAILED*') {
                $suppressions = [int]$s - [int]$baseSuppress
            }

            # The new cycle is any cycle that is not the stranded one.
            $pending = Sql ("select count(*) from operating_cycles where id <> '$OldCycle' " +
                            "and stopped_at_utc is null")
            $made = Sql "select count(*) from operating_cycles where id <> '$OldCycle'"

            if ($made -notlike 'QUERY FAILED*' -and [int]$made -gt 0 -and $pending -eq '0') {
                $newCycleTerminal = $true
            }

            if ($newCycleTerminal -and $suppressions -ge $RequiredSuppressions) {
                Say ('  ' + (Stamp) + '  new cycle is terminal and ' + $suppressions +
                     ' cooldown suppressions recorded. Enough.')
                break
            }
        }

        Say ('  ' + (Stamp) + '  observation finished. new cycle terminal = ' + $newCycleTerminal +
             ', suppressions = ' + $suppressions)
    }
}
catch {
    Say ''
    Say ('  UNEXPECTED: ' + $_.Exception.Message)
    $exitCode = 1
}
finally {
    # ----------------------------------------------------------------------------------
    # 4. Stop the API. Always.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 4. STOP THE API ---'

    if ($null -ne $api) {
        try {
            if (-not $api.HasExited) {
                # Stopped at a quiescent point: every stage is persisted as it completes and a
                # lease dies with the process, which is what the design relies on for restarts.
                Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 4
            }
            Say ('  pid ' + $api.Id + ' stopped ' + (Stamp))
        }
        catch { Say ('  could not stop: ' + $_.Exception.Message) }
    }
    else { Say '  never started.' }

    $still = @(Get-NetTCPConnection -LocalPort 5143 -State Listen -ErrorAction SilentlyContinue)
    Say ('  port 5143 still listening : ' + ($still.Count -gt 0))
    $procs = @(Get-Process -Name 'AI.Investment.Api' -ErrorAction SilentlyContinue)
    Say ('  AI.Investment.Api processes remaining : ' + $procs.Count)

    Remove-Item Env:\OperationsHost__RunCycles -ErrorAction SilentlyContinue
    Remove-Item Env:\ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
    Remove-Item Env:\ASPNETCORE_URLS -ErrorAction SilentlyContinue
    Say '  RunCycles cleared. It was never persisted to configuration.'
    Save-Log
}

# --------------------------------------------------------------------------------------
# 5. Verification, from the database, with the API down.
# --------------------------------------------------------------------------------------
Say ''
Say '--- 5. VERIFICATION ---'

Show 'ALL CYCLES (id | status | stage | started | stopped | reason)' (
    "select id, status, stage, started_at_utc, coalesce(stopped_at_utc::text,'-'), " +
    "coalesce(left(stopped_reason, 60),'-') from operating_cycles order by started_at_utc")

Show 'THE STRANDED PRE-FIX CYCLE, AFTER' (
    "select status, stage, coalesce(stopped_at_utc::text,'-'), coalesce(stopped_reason,'-') " +
    "from operating_cycles where id = '$OldCycle'")

Show 'POST-FIX CYCLES ONLY (id | status | stage | trigger_key)' (
    "select id, status, stage, trigger_key from operating_cycles where id <> '$OldCycle' " +
    "order by started_at_utc")

Show 'DUPLICATE trigger_key (must be none)' (
    'select trigger_key, count(*) from operating_cycles group by trigger_key having count(*) > 1')

Show 'INGESTION RUNS (id | source | outcome | reason | started | completed)' (
    "select id, source_id, outcome, coalesce(left(reason,40),'-'), started_at_utc, " +
    "coalesce(completed_at_utc::text,'-') from ingestion_runs order by started_at_utc")

Show 'THE OWNED GRAPH - request + subject columns of every run (THE FIX)' (
    'select source_id, category, region, subject_kind, subject_identifier, correlation_id, ' +
    'left(request_fingerprint, 16), requested_at_utc from ingestion_runs order by started_at_utc')

Show 'ARTIFACTS FETCHED per run (jsonb length > 2 means bytes were archived)' (
    'select id, length(artifacts::text), artifacts::text from ingestion_runs order by started_at_utc')

Show 'ACTION EXECUTIONS (action_type | started | completed | status)' (
    "select action_type, started_at_utc, coalesce(completed_at_utc::text,'-'), status " +
    'from action_executions order by started_at_utc desc limit 12')

Show 'AUDIT BY EVENT TYPE (event_type | count | newest)' (
    'select event_type, count(*), max(occurred_at_utc) from audit_records ' +
    'group by event_type order by max(occurred_at_utc) desc')

Show 'COOLDOWN SUPPRESSIONS (newest 8) - the live proof the 4-hour rule holds' (
    "select occurred_at_utc, left(summary, 110) from audit_records " +
    "where event_type = 'WatchSuppressed' order by occurred_at_utc desc limit 8")

Show 'WATCH FIRED records' (
    "select occurred_at_utc, left(summary, 90) from audit_records " +
    "where event_type = 'WatchFired' order by occurred_at_utc desc limit 5")

Show 'CYCLE COMPLETED / FAILED records' (
    "select occurred_at_utc, event_type, left(summary, 90) from audit_records " +
    "where event_type in ('CycleCompleted','CycleFailed','CycleSuspended') " +
    'order by occurred_at_utc desc limit 8')

Show 'OBSERVATIONS (count | newest)' 'select count(*), max(retrieved_at_utc) from observations'

Show 'OBSERVATIONS sample (subject | attribute | value)' (
    'select subject_identifier, attribute, value, source_id from observations ' +
    'order by retrieved_at_utc desc limit 8')

Show 'ESCALATIONS (raised | reason | cycle)' (
    "select raised_at_utc, left(reason, 60), coalesce(cycle_id::text,'-') from escalations " +
    'order by raised_at_utc desc limit 8')

Show 'OUTBOX (status | count)' 'select status, count(*) from outbox_messages group by status'

Show 'OUTBOX newest (message_type | status | attempts | occurred | dispatched)' (
    "select message_type, status, attempts, occurred_at_utc, " +
    "coalesce(dispatched_at_utc::text,'-') from outbox_messages " +
    'order by occurred_at_utc desc limit 8')

Show 'THE WATCH, AFTER (enabled | fire_count | last_fired | cooldown | interval)' (
    "select enabled, fire_count, last_fired_at_utc, cooldown::text, condition_interval::text " +
    "from watches where id = '$WatchId'")

Show 'COOLDOWN NOW (expires | now | elapsed | seconds_remaining)' (
    "select last_fired_at_utc + cooldown, now(), (now() >= last_fired_at_utc + cooldown), " +
    "greatest(0, ceil(extract(epoch from (last_fired_at_utc + cooldown) - now())))::bigint " +
    "from watches where id = '$WatchId'")

# --------------------------------------------------------------------------------------
# 6. The API's own log.
# --------------------------------------------------------------------------------------
Say ''
Say '--- 6. API LOG ---'
Say ('  stdout : ' + $apiOut)

Say ''
Say '  UNAUTHORISED WRITE CHECK'
$bad = @()
foreach ($f in @($apiOut, $apiErr)) {
    if (Test-Path $f) { $bad += @(Select-String -Path $f -Pattern 'UnauthorizedWriteException' -SimpleMatch -ErrorAction SilentlyContinue) }
}
if ($bad.Count -eq 0) { Say '    PASS  no UnauthorizedWriteException in this run.' }
else {
    Say ('    FAIL  ' + $bad.Count + ' occurrences:')
    foreach ($b in @($bad | Select-Object -First 8)) { Say ('      ' + $b.Line.Trim()) }
    $exitCode = 1
}

Say ''
Say '  EODHD REQUEST EVIDENCE'
$eod = @()
if (Test-Path $apiOut) { $eod = @(Select-String -Path $apiOut -Pattern 'eodhd' -ErrorAction SilentlyContinue | Select-Object -First 15) }
if ($eod.Count -eq 0) { Say '    (nothing matched "eodhd" in the log)' }
foreach ($m in $eod) { Say ('    ' + $m.Line.Trim()) }

Say ''
Say '  CYCLE / SCHEDULE LINES (newest 40)'
$interesting = @()
if (Test-Path $apiOut) {
    $interesting = @(Select-String -Path $apiOut -Pattern 'cycle|Cycle|watch|Watch|schedule|Schedule|Ingest|escalat|Escalat|outbox|Outbox|error|Error|fail|Fail|Exception' `
        -ErrorAction SilentlyContinue | Select-Object -Last 40)
}
if ($interesting.Count -eq 0) { Say '    (nothing matched)' }
foreach ($m in $interesting) { Say ('    ' + $m.Line.Trim()) }

Say ''
Say '  ERRORS AND WARNINGS IN stderr'
$errs = @()
if (Test-Path $apiErr) { $errs = @(Get-Content $apiErr -ErrorAction SilentlyContinue | Select-Object -First 40) }
if ($errs.Count -eq 0) { Say '    (stderr empty)' }
foreach ($l in $errs) { Say ('    ' + $l) }

Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue

Say ''
Say '=== END ==='
Say '  The API is stopped. RunCycles was an environment variable of that process only.'

Save-Log
Write-Host ''
Write-Host ('Written: ' + $log)
exit $exitCode
