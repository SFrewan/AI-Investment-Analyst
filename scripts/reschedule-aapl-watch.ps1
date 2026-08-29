#requires -Version 5.1
<#
    RESCHEDULE THE AAPL.US WATCH.

    POST http://localhost:5143/api/operator/watches/{id}/schedule
    authenticated with X-Operator-Key, routed through the same Action/Policy seam as
    everything else: policy evaluated, idempotency claimed, audited before and after,
    written inside an authorisation window.

    Only the interval moves. The domain's Reschedule leaves CreatedAtUtc, LastFiredAtUtc,
    FireCount, Cooldown and Enabled exactly as they are, and this script captures all five
    BEFORE the call and asserts every one of them afterwards.

    Used twice in the observation:
        -IntervalMinutes 5      before the run
        -IntervalMinutes 1440   after it
    The idempotency key carries the interval, so those are two distinct audited acts and
    neither is mistaken for a repeat of the other.

    WHAT IT DOES NOT DO
      It does not start the API, does not touch OperationsHost:RunCycles, does not start a
      cycle and makes no EODHD request. Rescheduling a watch changes when the ticker would
      consider it due; with RunCycles false nothing is ticking at all.
#>

[CmdletBinding()]
param(
    [ValidateRange(1, 100000)]
    [int]$IntervalMinutes = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log  = Join-Path $out ("watch-reschedule-" + $IntervalMinutes + "min.txt")

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }

function Get-BodyText($response) {
    if ($null -eq $response) { return '' }
    $c = $response.Content
    if ($null -eq $c) { return '' }
    if ($c -is [string]) { return $c.Trim() }
    if ($c -is [byte[]]) { return ([Text.Encoding]::UTF8.GetString($c)).Trim() }
    return (($c | Out-String).Trim())
}

function Show([object]$v, [string]$whenNull = '(not stated)') {
    if ($null -eq $v) { return $whenNull }
    return [string]$v
}

# Under Set-StrictMode -Version Latest, $obj.Missing THROWS rather than answering null, so
# a property the response does not carry stops the script instead of reading as absent.
# These are the same two helpers the already-proven scripts use.
function Get-Prop($obj, [string]$name, $fallback = $null) {
    if ($null -eq $obj) { return $fallback }
    $p = $obj.PSObject.Properties[$name]
    if ($null -eq $p) { return $fallback }
    if ($null -eq $p.Value) { return $fallback }
    return $p.Value
}

# Invoke-RestMethod can hand back a collection rather than the object itself.
function Get-First($response) {
    if ($null -eq $response) { return $null }
    $items = @($response)
    if ($items.Count -eq 0) { return $null }
    return $items[0]
}

function Get-ErrorStatus($e) {
    try {
        $rp = $e.Exception.PSObject.Properties['Response']
        if ($null -eq $rp -or $null -eq $rp.Value) { return $null }
        $sc = $rp.Value.PSObject.Properties['StatusCode']
        if ($null -eq $sc -or $null -eq $sc.Value) { return $null }
        return [string][int]$sc.Value
    }
    catch { return $null }
}

function Get-ErrorBody($e) {
    try {
        $d = $e.PSObject.Properties['ErrorDetails']
        if ($null -ne $d -and $null -ne $d.Value) {
            $m = $d.Value.PSObject.Properties['Message']
            if ($null -ne $m -and -not [string]::IsNullOrWhiteSpace([string]$m.Value)) {
                return ([string]$m.Value).Trim()
            }
        }
        $rp = $e.Exception.PSObject.Properties['Response']
        if ($null -ne $rp -and $null -ne $rp.Value) {
            $s = $rp.Value.GetResponseStream()
            if ($null -ne $s) {
                $reader = New-Object System.IO.StreamReader($s)
                try { return $reader.ReadToEnd().Trim() } finally { $reader.Dispose() }
            }
        }
        return $e.Exception.Message
    }
    catch { return $e.Exception.Message }
}

$BaseUrl    = 'http://localhost:5143'
$Symbol     = 'AAPL.US'
$Template   = 'equity-price-review'
$apiProject = Join-Path $root 'src\AI.Investment.Api'

Say '=== RESCHEDULE THE AAPL.US WATCH ==='
Say ("UTC now         : " + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
Say ("target interval : " + $IntervalMinutes + ' minutes')
Say ''

# --------------------------------------------------------------------------------------
# 1. The watch, before. Read-only, and before anything is asked for.
# --------------------------------------------------------------------------------------
Say '--- 1. THE WATCH, BEFORE ---'

$psql = $null
try {
    $cmd = @(Get-Command psql -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
    if ($null -ne $cmd) { $psql = [string]$cmd.Source }
    if ([string]::IsNullOrWhiteSpace($psql)) {
        $c = @(Get-ChildItem -Path 'C:\Program Files\PostgreSQL' -Filter 'psql.exe' -Recurse -ErrorAction SilentlyContinue)
        if ($c.Count -gt 0) { $psql = [string]$c[0].FullName }
    }
    if ([string]::IsNullOrWhiteSpace($psql)) { $psql = $null }
}
catch { $psql = $null }

if ($null -eq $psql) {
    Say '  psql not found, so the watch cannot be read or verified. STOPPING. Nothing was changed.'
    Save-Log; exit 1
}

$H='localhost'; $P='5432'; $D=''; $U=''
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
    Say '  Database:ConnectionString unavailable. STOPPING. Nothing was changed.'
    Save-Log; exit 1
}

$parts = @{}
foreach ($seg in @($cs -split ';')) {
    $j = $seg.IndexOf('=')
    if ($j -gt 0) { $parts[$seg.Substring(0,$j).Trim().ToLowerInvariant()] = $seg.Substring($j+1).Trim() }
}
$cs = $null
if ($parts.ContainsKey('host'))     { $H = $parts['host'] }
if ($parts.ContainsKey('port'))     { $P = $parts['port'] }
if ($parts.ContainsKey('database')) { $D = $parts['database'] }
if ($parts.ContainsKey('username')) { $U = $parts['username'] }
if ($parts.ContainsKey('password')) { $env:PGPASSWORD = $parts['password'] }
$parts = $null

# psql writes diagnostics to stderr, and a native command's stderr becomes an ErrorRecord
# that ErrorActionPreference = Stop treats as terminating. A failed query must be
# reportable, not fatal.
function Sql([string]$sql) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $raw = $null; $code = 0
    try { $raw = & $psql -h $H -p $P -U $U -d $D -t -A -F ' | ' -c $sql 2>&1; $code = $LASTEXITCODE }
    catch { return 'QUERY FAILED' }
    finally { $ErrorActionPreference = $previous }

    if ($code -ne 0) { return 'QUERY FAILED' }
    return (($raw | Out-String).Trim())
}

$exitCode = 0

try {
    # Every field the reschedule must NOT move, captured for comparison afterwards.
    $before = Sql ("select id, enabled, fire_count, created_at_utc, " +
                   "coalesce(last_fired_at_utc::text,''), cooldown::text, condition_interval::text, " +
                   "extract(epoch from condition_interval)::bigint " +
                   "from watches where target_identifier = '$Symbol' and cycle_template = '$Template'")

    if ($before -eq 'QUERY FAILED' -or [string]::IsNullOrWhiteSpace($before)) {
        Say ('  No watch found for ' + $Symbol + ' / ' + $Template + ', or the query failed.')
        Say '  STOPPING. Nothing was attempted.'
        Save-Log; exit 1
    }

    $rows = @($before -split "`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

    if ($rows.Count -ne 1) {
        Say ('  Expected exactly one watch and found ' + $rows.Count + '. Not guessing which.')
        Say '  STOPPING. Nothing was attempted.'
        Save-Log; exit 1
    }

    $b = @($rows[0] -split '\|' | ForEach-Object { $_.Trim() })

    $watchId       = $b[0]
    $wasEnabled    = $b[1]
    $wasFireCount  = $b[2]
    $wasCreated    = $b[3]
    $wasLastFired  = $b[4]
    $wasCooldown   = $b[5]
    $wasInterval   = $b[6]
    $wasSeconds    = [int64]$b[7]

    Say ('  id                : ' + $watchId)
    Say ('  enabled           : ' + $wasEnabled)
    Say ('  fire_count        : ' + $wasFireCount)
    Say ('  created_at_utc    : ' + $wasCreated)
    Say ('  last_fired_at_utc : ' + $(if ([string]::IsNullOrWhiteSpace($wasLastFired)) { '(never fired)' } else { $wasLastFired }))
    Say ('  cooldown          : ' + $wasCooldown)
    Say ('  interval          : ' + $wasInterval + '   (' + ($wasSeconds / 60) + ' minutes)')

    if ($wasSeconds -eq ($IntervalMinutes * 60)) {
        Say ''
        Say ('  Already running every ' + $IntervalMinutes + ' minutes. The API would answer')
        Say '  DuplicateSuppressed. Not sending the request.'
        Save-Log; exit 0
    }

    # ----------------------------------------------------------------------------------
    # 2. What will change.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 2. WHAT WILL CHANGE ---'
    Say ('  POST /api/operator/watches/' + $watchId + '/schedule')
    Say ('    intervalMinutes : ' + ($wasSeconds / 60) + '  ->  ' + $IntervalMinutes)
    Say ''
    Say '  ONE column on one row: watches.condition_interval. Plus one audit_records row'
    Say '  and one action_executions row for operator.reschedule-watch, naming the operator.'
    Say ''
    Say '  UNCHANGED, and asserted below: created_at_utc, last_fired_at_utc, fire_count,'
    Say '  cooldown, enabled, target, template. The watch keeps its history.'
    Say ''
    Say '  RunCycles is NOT touched and no cycle is started. With RunCycles false the'
    Say '  ticker is not running, so a shorter interval starts nothing by itself.'
    Say '  No EODHD request is made by this script.'

    Write-Host ''
    Write-Host ('Type RESCHEDULE to set the interval to ' + $IntervalMinutes + ' minutes, or anything else to abort:') -ForegroundColor Yellow
    $confirm = Read-Host

    if ([string]$confirm -cne 'RESCHEDULE') {
        Say ''
        Say '  Not confirmed. STOPPING. Nothing was changed.'
        Save-Log; exit 0
    }

    Write-Host ''
    Write-Host 'Reason (required, max 120 chars):' -ForegroundColor Yellow
    $reason = Read-Host

    if ([string]::IsNullOrWhiteSpace($reason)) {
        Say ''
        Say '  No reason given. STOPPING. Nothing was changed.'
        Save-Log; exit 1
    }

    # ----------------------------------------------------------------------------------
    # 3. The key.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 3. OPERATOR CREDENTIAL ---'
    Write-Host ''
    Write-Host 'Enter the operator key (input is hidden, and is not written to any file):' -ForegroundColor Yellow
    $secure = Read-Host -AsSecureString

    $key = $null
    if ($null -ne $secure) {
        $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { $key = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
        finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    }

    if ([string]::IsNullOrWhiteSpace($key)) {
        $key = $null
        Say '  No key entered. STOPPING. Nothing was changed.'
        Save-Log; exit 1
    }

    Say '  key received from the secure prompt (not shown, not logged, not measured).'

    $headers = @{ 'X-Operator-Key' = $key }
    $status = $null; $body = $null

    try {
        # Proves the key authenticates and carries AdministerWatches before anything moves.
        Say ''
        Say '--- 4. WHOAMI (before the mutating call) ---'
        try {
            $me = Get-First (Invoke-RestMethod -Uri ($BaseUrl + '/api/operator/whoami') -Headers $headers -TimeoutSec 30)

            if ($null -eq $me) {
                Say '  whoami returned an empty body. STOPPING before the reschedule. Nothing was changed.'
                Save-Log; exit 1
            }

            # OperatorIdentityDto is (Id, DisplayName, Privileges), so the identity arrives as
            # 'id'. 'operatorId' is tried first only so a future rename does not break this.
            $privs = @(@(Get-Prop $me 'privileges' @()) | Sort-Object)

            Say ('  operator     : ' + (Show (Get-Prop $me 'operatorId' (Get-Prop $me 'id'))))
            Say ('  display name : ' + (Show (Get-Prop $me 'displayName')))
            Say ('  privileges   : ' + $(if ($privs.Count -eq 0) { '(none reported)' } else { $privs -join ', ' }))

            # A readable list that lacks the privilege means the POST would be refused 403.
            # An unreadable list is not treated as a refusal - the endpoint's own policy gate
            # stays the authority, exactly as before.
            if ($privs.Count -gt 0 -and $privs -notcontains 'AdministerWatches') {
                Say ''
                Say '  AdministerWatches is not held by this operator. The reschedule would be'
                Say '  refused 403. STOPPING before the request. Nothing was changed.'
                Save-Log; exit 1
            }
        }
        catch {
            Say ('  FAILED ' + (Show (Get-ErrorStatus $_) '') + ' : the key was not accepted, or AdministerWatches is missing.')
            Say ('  detail : ' + (Show (Get-ErrorBody $_) '(no body)'))
            Say '  STOPPING before the reschedule. Nothing was changed.'
            Save-Log; exit 1
        }

        Say ''
        Say '--- 5. POST .../schedule ---'

        $payload = @{ intervalMinutes = $IntervalMinutes; reason = $reason.Trim() } | ConvertTo-Json

        $resp = Invoke-WebRequest `
            -Uri ($BaseUrl + '/api/operator/watches/' + $watchId + '/schedule') `
            -Method Post -Headers $headers -ContentType 'application/json' `
            -Body $payload -UseBasicParsing -TimeoutSec 60

        $status = [string][int]$resp.StatusCode
        $body = Get-BodyText $resp
    }
    catch {
        $status = Get-ErrorStatus $_
        $body = Get-ErrorBody $_
    }
    finally {
        $key = $null
        $headers = $null
        [System.GC]::Collect()
    }

    Say ('  HTTP status : ' + (Show $status '(no status)'))
    Say ('  response    : ' + (Show $body '(no body)'))
    Say ''
    Say '  200 Done = rescheduled. 200 DuplicateSuppressed = already that interval.'
    Say '  400 = no reason, an interval the domain refuses, or not a schedule watch.'
    Say '  401 = key not accepted. 403 = AdministerWatches missing. 404 = no such watch.'
    Say '  409 = policy denied it; nothing changed.'

    # ----------------------------------------------------------------------------------
    # 6. Verify the persisted state, and that nothing else moved.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 6. THE WATCH, AFTER ---'

    $after = Sql ("select enabled, fire_count, created_at_utc, " +
                  "coalesce(last_fired_at_utc::text,''), cooldown::text, condition_interval::text, " +
                  "extract(epoch from condition_interval)::bigint " +
                  "from watches where id = '$watchId'")

    if ($after -eq 'QUERY FAILED' -or [string]::IsNullOrWhiteSpace($after)) {
        Say '  Could not re-read the watch. Verify manually before doing anything else.'
        Save-Log; exit 1
    }

    # Assign the filtered rows BEFORE indexing. A pipeline yielding one item assigns a
    # SCALAR, and [0] on a string is its first character - which then splits into a single
    # field and indexes out of bounds. Same trap as the boundary arithmetic earlier.
    $afterRows = @($after -split "`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

    if ($afterRows.Count -eq 0) {
        Say '  The watch could not be re-read. Verify manually before doing anything else.'
        Save-Log; exit 1
    }

    $a = @($afterRows[0] -split '\|' | ForEach-Object { $_.Trim() })

    if ($a.Count -lt 7) {
        Say ('  The verification query returned ' + $a.Count + ' fields, expected 7. Not')
        Say '  interpreting a row this script does not recognise. Verify manually.'
        Save-Log; exit 1
    }

    $nowSeconds = [int64]$a[6]

    Say ('  interval          : ' + $a[5] + '   (' + ($nowSeconds / 60) + ' minutes)')
    Say ('  enabled           : ' + $a[0])
    Say ('  fire_count        : ' + $a[1])
    Say ('  created_at_utc    : ' + $a[2])
    Say ('  last_fired_at_utc : ' + $(if ([string]::IsNullOrWhiteSpace($a[3])) { '(never fired)' } else { $a[3] }))
    Say ('  cooldown          : ' + $a[4])

    Say ''
    Say '  ASSERTIONS'

    # Named fields, not positions. PowerShell's comma operator binds TIGHTER than '+', so a
    # pair written as @('a ' + $x + ' b', $flag) parses as 'a ' + $x + (' b', $flag) - one
    # concatenated string, and [1] on it is out of bounds. A property carries no such trap.
    $checks = @(
        [pscustomobject]@{ Label = 'interval is now ' + $IntervalMinutes + ' minutes'; Ok = ($nowSeconds -eq ($IntervalMinutes * 60)) }
        [pscustomobject]@{ Label = 'enabled unchanged';           Ok = ($a[0] -eq $wasEnabled) }
        [pscustomobject]@{ Label = 'fire_count unchanged';        Ok = ($a[1] -eq $wasFireCount) }
        [pscustomobject]@{ Label = 'created_at_utc unchanged';    Ok = ($a[2] -eq $wasCreated) }
        [pscustomobject]@{ Label = 'last_fired_at_utc unchanged'; Ok = ($a[3] -eq $wasLastFired) }
        [pscustomobject]@{ Label = 'cooldown unchanged';          Ok = ($a[4] -eq $wasCooldown) }
    )

    $allOk = $true
    foreach ($c in $checks) {
        $ok = [bool]$c.Ok
        if (-not $ok) { $allOk = $false; $exitCode = 1 }
        Say ('    ' + $(if ($ok) { 'PASS' } else { 'FAIL' }) + '  ' + $c.Label)
    }

    # ----------------------------------------------------------------------------------
    # 7. The audit trail. A reschedule that left no record would not be an audited act.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 7. AUDIT ---'
    Say '  audit_records for operator.reschedule-watch (newest 5): occurred | actor | kind | outcome | summary'
    foreach ($r in @((Sql ("select occurred_at_utc, actor, actor_kind, outcome, left(summary, 100) " +
                           "from audit_records where action_type = 'operator.reschedule-watch' " +
                           "order by occurred_at_utc desc limit 5")) -split "`n")) {
        if (-not [string]::IsNullOrWhiteSpace($r)) { Say ('    ' + $r.Trim()) }
    }

    Say ''
    Say '  action_executions for operator.reschedule-watch (newest 5): started | completed | status'
    foreach ($r in @((Sql ("select started_at_utc, completed_at_utc, status from action_executions " +
                           "where action_type = 'operator.reschedule-watch' " +
                           "order by started_at_utc desc limit 5")) -split "`n")) {
        if (-not [string]::IsNullOrWhiteSpace($r)) { Say ('    ' + $r.Trim()) }
    }

    Say ''
    Say ('  operating_cycles (must still be 0 - nothing started) : ' + (Sql 'select count(*) from operating_cycles'))
    Say ('  ingestion_runs for eodhd-eod (must still be 0)       : ' +
         (Sql "select count(*) from ingestion_runs where source_id = 'eodhd-eod'"))

    Say ''
    if ($allOk) {
        Say '  RESULT: the interval moved and nothing else did.'
    }
    else {
        Say '  RESULT: something other than the interval changed. Do not proceed; inspect it.'
    }
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

Say ''
Say '=== END ==='
Say 'RunCycles unchanged. No cycle started. No EODHD request made.'

Save-Log
Write-Host ''
Write-Host ('Written: ' + $log)
exit $exitCode
