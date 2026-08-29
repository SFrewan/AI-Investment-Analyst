#requires -Version 5.1
<#
    SELF-LIMITING LIVE OBSERVATION RUN.

    Waits until the AAPL.US watch is out of cooldown, starts the API with
    OperationsHost__RunCycles=true on http://localhost:5143, watches for the scheduled
    cycle, stops the API itself, then verifies the whole path from the database.

    IT STOPS ITSELF. The API is started as one process and killed by this script when the
    observation window closes, so nothing is left running unattended.

    WHY IT WAITS
      Watch.Evaluate refuses WithinCooldown before it even looks at the condition. The
      watch's cooldown is four hours and it last fired at the failed cycle, so it cannot
      fire again until that has elapsed - whatever the five-minute interval says.
#>

[CmdletBinding()]
param(
    [ValidateRange(1, 120)]
    [int]$ObserveMinutes = 15,

    [ValidateRange(0, 480)]
    [int]$MaxWaitMinutes = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root   = Split-Path -Parent $PSScriptRoot
$out    = Join-Path $root 'artifacts\verify'
$null   = New-Item -ItemType Directory -Force -Path $out
$log    = Join-Path $out 'live-observation.txt'
$apiOut = Join-Path $out 'live-api.log'
$apiErr = Join-Path $out 'live-api.err.log'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }
function Stamp { return (Get-Date).ToUniversalTime().ToString('HH:mm:ss') + 'Z' }

$Symbol   = 'AAPL.US'
$Template = 'equity-price-review'
$BaseUrl  = 'http://localhost:5143'

# --------------------------------------------------------------------------------------
# psql. Failures are reportable, never terminating.
# --------------------------------------------------------------------------------------
$psql = $null
$found = @(Get-Command psql -CommandType Application -ErrorAction SilentlyContinue)
if ($found.Count -gt 0) { $psql = [string]$found[0].Source }
if ([string]::IsNullOrWhiteSpace($psql)) {
    $candidates = @(Get-ChildItem -Path 'C:\Program Files\PostgreSQL' -Filter 'psql.exe' -Recurse -ErrorAction SilentlyContinue)
    if ($candidates.Count -gt 0) { $psql = [string]$candidates[0].FullName }
}

$H = 'localhost'; $P = '5432'; $D = ''; $U = ''

function Sql([string]$sql) {
    if ([string]::IsNullOrWhiteSpace($psql)) { return 'NO PSQL' }
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $raw = $null; $code = 0
    try { $raw = & $psql -h $H -p $P -U $U -d $D -t -A -F ' | ' -c $sql 2>&1; $code = $LASTEXITCODE }
    catch { return 'QUERY FAILED' }
    finally { $ErrorActionPreference = $previous }
    if ($code -ne 0) { return ('QUERY FAILED: ' + (($raw | Out-String).Trim())) }
    return (($raw | Out-String).Trim())
}

function SqlRows([string]$sql) {
    $text = Sql $sql
    if ($text -like 'QUERY FAILED*' -or $text -eq 'NO PSQL') { return @($text) }
    return @($text -split "`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Show-Rows([string]$title, [string]$sql) {
    Say ('  ' + $title)
    $rows = @(SqlRows $sql)
    if ($rows.Count -eq 0) { Say '      (none)' ; return }
    foreach ($r in $rows) { Say ('      ' + ([string]$r).Trim()) }
}

$exitCode = 0
$api = $null

Say '=== LIVE OBSERVATION RUN (self-limiting) ==='
Say ('UTC now : ' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
Say ('repo    : ' + $root)
Save-Log

try {
    # ----------------------------------------------------------------------------------
    # 0. Preflight.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 0. PREFLIGHT ---'

    if ([string]::IsNullOrWhiteSpace($psql)) {
        Say '  psql not found. Nothing could be verified afterwards. STOPPING before starting anything.'
        Save-Log; exit 1
    }
    Say ('  psql   : ' + $psql)

    $apiExe = Join-Path $root 'src\AI.Investment.Api\bin\Release\net8.0\AI.Investment.Api.exe'
    if (-not (Test-Path $apiExe)) {
        Say ('  Release binary missing: ' + $apiExe)
        Say '  STOPPING. Build first.'
        Save-Log; exit 1
    }
    Say ('  api    : ' + $apiExe)

    $busy = @(Get-NetTCPConnection -LocalPort 5143 -State Listen -ErrorAction SilentlyContinue)
    if ($busy.Count -gt 0) {
        Say '  Something is already listening on 5143. STOPPING rather than fighting it.'
        Save-Log; exit 1
    }
    Say '  port 5143 free : True'

    $apiProject = Join-Path $root 'src\AI.Investment.Api'
    $cs = $null; $text = $null
    try {
        $text = (& dotnet user-secrets list --project $apiProject 2>&1) -join "`n"
        if ($LASTEXITCODE -eq 0) {
            foreach ($line in @($text -split "`n")) {
                $i = $line.IndexOf(' = ')
                if ($i -gt 0 -and $line.Substring(0, $i).Trim() -eq 'Database:ConnectionString') {
                    $cs = $line.Substring($i + 3)
                }
            }
        }
    }
    catch { }
    finally { $text = $null }

    if ([string]::IsNullOrWhiteSpace($cs)) {
        Say '  Database:ConnectionString unavailable. STOPPING.'
        Save-Log; exit 1
    }

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
    Say ('  database : ' + $D + ' on ' + $H + ':' + $P)
    Save-Log

    # ----------------------------------------------------------------------------------
    # 1. The watch, before. And when it may next fire.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 1. THE WATCH, BEFORE ---'

    $before = @(SqlRows ("select id, enabled, fire_count, coalesce(last_fired_at_utc::text,''), " +
                         "extract(epoch from cooldown)::bigint, " +
                         "extract(epoch from condition_interval)::bigint, " +
                         "extract(epoch from max_signal_age)::bigint " +
                         "from watches where target_identifier = '$Symbol' and cycle_template = '$Template'"))

    if ($before.Count -ne 1 -or $before[0] -like 'QUERY FAILED*' -or $before[0] -eq 'NO PSQL') {
        Say ('  Expected exactly one watch, got: ' + ($before -join ' / '))
        Say '  STOPPING. Nothing was started.'
        Save-Log; exit 1
    }

    $w = @($before[0] -split '\|' | ForEach-Object { $_.Trim() })
    if ($w.Count -lt 7) {
        Say '  Unrecognised watch row. STOPPING.'
        Save-Log; exit 1
    }

    $watchId       = $w[0]
    $wasEnabled    = $w[1]
    $wasFireCount  = $w[2]
    $wasLastFired  = $w[3]
    $cooldownSec   = [int64]$w[4]
    $intervalSec   = [int64]$w[5]
    $signalAgeSec  = [int64]$w[6]

    Say ('  id                : ' + $watchId)
    Say ('  enabled           : ' + $wasEnabled)
    Say ('  fire_count        : ' + $wasFireCount)
    Say ('  last_fired_at_utc : ' + $(if ([string]::IsNullOrWhiteSpace($wasLastFired)) { '(never)' } else { $wasLastFired }))
    Say ('  cooldown          : ' + ($cooldownSec / 60) + ' minutes')
    Say ('  interval          : ' + ($intervalSec / 60) + ' minutes')
    Say ('  max_signal_age    : ' + ($signalAgeSec / 60) + ' minutes')

    if ($wasEnabled -ne 't') {
        Say '  The watch is not enabled. STOPPING; nothing would fire.'
        Save-Log; exit 1
    }

    # When cooldown lets it fire again. Asked of the database so its clock decides, not ours.
    $eligible = Sql ("select to_char(coalesce(last_fired_at_utc, now()) + cooldown, " +
                     "'YYYY-MM-DD HH24:MI:SS') || '|' || " +
                     "greatest(0, ceil(extract(epoch from " +
                     "(coalesce(last_fired_at_utc, now()) + cooldown) - now())))::bigint " +
                     "from watches where id = '$watchId'")

    $waitSeconds = 0
    if ($eligible -notlike 'QUERY FAILED*' -and $eligible -ne 'NO PSQL' -and $eligible.Contains('|')) {
        $e = @($eligible -split '\|' | ForEach-Object { $_.Trim() })
        if ($e.Count -ge 2) {
            Say ('  next eligible     : ' + $e[0] + ' UTC (database clock)')
            $waitSeconds = [int64]$e[1]
        }
    }

    Say ''
    Say '  COOLDOWN GOVERNS, NOT THE INTERVAL.'
    Say '  Watch.Evaluate checks cooldown before the condition, so a 4-hour cooldown means at'
    Say '  most one firing per 4 hours no matter what the 5-minute interval says. Two or three'
    Say '  cycles in one sitting is not observable without changing the cooldown, and there is'
    Say '  no audited operator action for cooldown - only for the interval. So this run observes'
    Say '  ONE firing, and then proves the cooldown refuses the ticks that follow it.'
    Save-Log

    if ($waitSeconds -gt ($MaxWaitMinutes * 60)) {
        Say ''
        Say ('  Cooldown has ' + [math]::Round($waitSeconds / 60.0, 1) + ' minutes left, which is more than the ' +
             $MaxWaitMinutes + '-minute cap. STOPPING; nothing was started.')
        Save-Log; exit 1
    }

    if ($waitSeconds -gt 0) {
        $target = (Get-Date).ToUniversalTime().AddSeconds($waitSeconds + 45)
        Say ''
        Say ('--- WAITING FOR COOLDOWN: ' + [math]::Round($waitSeconds / 60.0, 1) + ' minutes ---')
        Say ('  The API is NOT started during this wait. Nothing is running.')
        Say ('  Resuming at about ' + $target.ToString('HH:mm:ss') + 'Z')
        Save-Log

        while ((Get-Date).ToUniversalTime() -lt $target) {
            Start-Sleep -Seconds 60
            $left = [math]::Round(($target - (Get-Date).ToUniversalTime()).TotalMinutes, 1)
            if ($left -gt 0) { Write-Host ('    ' + (Stamp) + '  ' + $left + ' minutes left') }
        }
        Say ('  ' + (Stamp) + '  cooldown elapsed.')
        Save-Log
    }

    # ----------------------------------------------------------------------------------
    # 2. Counts before, so anything new is provably new.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 2. COUNTS BEFORE ---'
    $cyclesBefore     = Sql 'select count(*) from operating_cycles'
    $runsBefore       = Sql "select count(*) from ingestion_runs where source_id = 'eodhd-eod'"
    $auditBefore      = Sql 'select count(*) from audit_records'
    $execBefore       = Sql 'select count(*) from action_executions'
    $observationsBefore = Sql 'select count(*) from observations'
    $escalationsBefore  = Sql 'select count(*) from escalations'

    Say ('  operating_cycles          : ' + $cyclesBefore)
    Say ('  ingestion_runs eodhd-eod  : ' + $runsBefore)
    Say ('  audit_records             : ' + $auditBefore)
    Say ('  action_executions         : ' + $execBefore)
    Say ('  observations              : ' + $observationsBefore)
    Say ('  escalations               : ' + $escalationsBefore)
    Save-Log

    # ----------------------------------------------------------------------------------
    # 3. Start the API. One process, so stopping it is unambiguous.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 3. START THE API ---'
    Say '  OperationsHost__RunCycles = true'
    Say '  ASPNETCORE_ENVIRONMENT    = Development'
    Say ('  ASPNETCORE_URLS           = ' + $BaseUrl)
    Say '  Running the Release binary directly rather than through dotnet run, so the API is'
    Say '  one process this script can stop cleanly instead of a child it might orphan.'

    $env:OperationsHost__RunCycles = 'true'
    $env:ASPNETCORE_ENVIRONMENT    = 'Development'
    $env:ASPNETCORE_URLS           = $BaseUrl

    Remove-Item $apiOut -ErrorAction SilentlyContinue
    Remove-Item $apiErr -ErrorAction SilentlyContinue

    $api = Start-Process -FilePath $apiExe `
        -WorkingDirectory (Join-Path $root 'src\AI.Investment.Api') `
        -RedirectStandardOutput $apiOut `
        -RedirectStandardError $apiErr `
        -PassThru -WindowStyle Hidden

    Say ('  started pid ' + $api.Id + ' at ' + (Stamp))
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
        Say '  The API did not answer /health within 2 minutes. Stopping it and reporting.'
        Say '  --- api stderr ---'
        foreach ($l in @(Get-Content $apiErr -ErrorAction SilentlyContinue | Select-Object -First 60)) { Say ('    ' + $l) }
        Say '  --- api stdout (tail) ---'
        foreach ($l in @(Get-Content $apiOut -ErrorAction SilentlyContinue | Select-Object -Last 60)) { Say ('    ' + $l) }
        $exitCode = 1
    }
    else {
        Say ('  healthy at ' + (Stamp))
        Save-Log

        # ------------------------------------------------------------------------------
        # 4. Watch for the cycle. Poll the database rather than trusting the log.
        # ------------------------------------------------------------------------------
        Say ''
        Say ('--- 4. OBSERVING FOR ' + $ObserveMinutes + ' MINUTES ---')

        $deadline = (Get-Date).AddMinutes($ObserveMinutes)
        $seenCycle = $false
        $lastLine = ''

        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 20

            $snapshot = Sql ("select (select count(*) from operating_cycles) || ' | ' || " +
                             "(select count(*) from ingestion_runs where source_id = 'eodhd-eod') || ' | ' || " +
                             "(select coalesce(max(status), '-') from operating_cycles) || ' | ' || " +
                             "(select fire_count from watches where id = '$watchId')")

            if ($snapshot -ne $lastLine) {
                Say ('  ' + (Stamp) + '  cycles | runs | status | fire_count  =  ' + $snapshot)
                $lastLine = $snapshot
                Save-Log
            }

            $nowCycles = Sql 'select count(*) from operating_cycles'
            if ($nowCycles -ne $cyclesBefore -and $nowCycles -notlike 'QUERY FAILED*') { $seenCycle = $true }

            # Once a cycle exists and has stopped, there is nothing more to wait for except
            # the cooldown refusals, and two minutes of those is enough to show the pattern.
            if ($seenCycle) {
                $running = Sql "select count(*) from operating_cycles where stopped_at_utc is null"
                if ($running -eq '0' -and (Get-Date).AddMinutes(2) -lt $deadline) {
                    Say ('  ' + (Stamp) + '  cycle has stopped; holding two more minutes to see the cooldown refusals.')
                    Save-Log
                    $deadline = (Get-Date).AddMinutes(2)
                }
            }
        }

        Say ('  ' + (Stamp) + '  observation window closed.')
    }
}
catch {
    Say ''
    Say ('  UNEXPECTED: ' + $_.Exception.Message)
    $exitCode = 1
}
finally {
    # ----------------------------------------------------------------------------------
    # 5. Stop the API. Always, on every path.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 5. STOP THE API ---'

    if ($null -ne $api) {
        try {
            if (-not $api.HasExited) {
                Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 3
            }
            Say ('  pid ' + $api.Id + ' stopped at ' + (Stamp))
        }
        catch { Say ('  could not stop pid ' + $api.Id + ': ' + $_.Exception.Message) }
    }
    else {
        Say '  the API was never started.'
    }

    $still = @(Get-NetTCPConnection -LocalPort 5143 -State Listen -ErrorAction SilentlyContinue)
    Say ('  port 5143 still listening : ' + ($still.Count -gt 0))

    Remove-Item Env:\OperationsHost__RunCycles -ErrorAction SilentlyContinue
    Remove-Item Env:\ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
    Remove-Item Env:\ASPNETCORE_URLS -ErrorAction SilentlyContinue
    Say '  OperationsHost__RunCycles cleared. Nothing is running.'
    Save-Log
}

# --------------------------------------------------------------------------------------
# 6. Verification, from the database, after the API is down.
# --------------------------------------------------------------------------------------
Say ''
Say '--- 6. VERIFICATION ---'

Show-Rows 'watch after: enabled | fire_count | last_fired_at_utc | interval | cooldown' (
    "select enabled, fire_count, coalesce(last_fired_at_utc::text,'(never)'), " +
    "condition_interval::text, cooldown::text from watches where target_identifier = '$Symbol'")

Say ''
Show-Rows 'operating_cycles (newest 5): id | status | stage | started | stopped | reason | trigger_key' (
    "select id, status, stage, started_at_utc, coalesce(stopped_at_utc::text,'-'), " +
    "coalesce(stopped_reason,'-'), trigger_key from operating_cycles " +
    "order by started_at_utc desc limit 5")

Say ''
Show-Rows 'DUPLICATE CHECK - trigger_key values used more than once (must be none)' (
    "select trigger_key, count(*) from operating_cycles group by trigger_key having count(*) > 1")

Say ''
Show-Rows 'ingestion_runs eodhd-eod (newest 5): id | outcome | reason | subject | started | completed' (
    "select id, outcome, coalesce(reason,'-'), subject_identifier, started_at_utc, " +
    "coalesce(completed_at_utc::text,'-') from ingestion_runs where source_id = 'eodhd-eod' " +
    "order by started_at_utc desc limit 5")

Say ''
Show-Rows 'the newest run persisted its owned rows (request + subject columns must be populated)' (
    "select source_id, category, region, subject_kind, subject_identifier, correlation_id, " +
    "left(request_fingerprint, 16) from ingestion_runs where source_id = 'eodhd-eod' " +
    "order by started_at_utc desc limit 1")

Say ''
Show-Rows 'action_executions ingestion.fetch (newest 5): started | completed | status' (
    "select started_at_utc, coalesce(completed_at_utc::text,'-'), status from action_executions " +
    "where action_type = 'ingestion.fetch' order by started_at_utc desc limit 5")

Say ''
Show-Rows 'audit_records (newest 12): occurred | action_type | outcome | summary' (
    "select occurred_at_utc, action_type, outcome, left(summary, 70) from audit_records " +
    "order by occurred_at_utc desc limit 12")

Say ''
Show-Rows 'observations recorded (count)' 'select count(*) from observations'

Say ''
Show-Rows 'escalations (newest 5): raised | reason | cycle' (
    "select raised_at_utc, left(reason, 60), coalesce(cycle_id::text,'-') from escalations " +
    "order by raised_at_utc desc limit 5")

Say ''
Say '--- 7. THE API LOG ---'
Say ('  full stdout : ' + $apiOut)

$interesting = @()
if (Test-Path $apiOut) {
    $interesting = @(Select-String -Path $apiOut -Pattern 'UnauthorizedWrite|Unauthorized|fired|cycle|Ingest|eodhd|EODHD|policy|Policy|escalat|Escalat|error|Error|fail|Fail|exception|Exception' -ErrorAction SilentlyContinue |
        Select-Object -Last 60)
}

if ($interesting.Count -eq 0) { Say '  (nothing matched, or the log is empty)' }
foreach ($m in $interesting) { Say ('    ' + $m.Line.Trim()) }

Say ''
Say '  UNAUTHORISED WRITE CHECK'
$bad = @()
if (Test-Path $apiOut) { $bad = @(Select-String -Path $apiOut -Pattern 'UnauthorizedWriteException' -ErrorAction SilentlyContinue) }
if (Test-Path $apiErr) { $bad += @(Select-String -Path $apiErr -Pattern 'UnauthorizedWriteException' -ErrorAction SilentlyContinue) }

if ($bad.Count -eq 0) {
    Say '    PASS  no UnauthorizedWriteException anywhere in the run.'
}
else {
    Say ('    FAIL  ' + $bad.Count + ' occurrences:')
    foreach ($b in @($bad | Select-Object -First 10)) { Say ('      ' + $b.Line.Trim()) }
    $exitCode = 1
}

Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue

Say ''
Say '=== END ==='
Say '  The API is stopped. RunCycles is not persisted anywhere - it was an environment'
Say '  variable of that one process and died with it.'

Save-Log
Write-Host ''
Write-Host ('Written: ' + $log)
exit $exitCode
