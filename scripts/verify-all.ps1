#requires -Version 5.1
<#
    IDEMPOTENCY SCOPE FIX: BUILD, TEST, THEN LIVE END-TO-END VERIFICATION.

    One transcript, one run. Tests gate the live phase: if anything is red the API is never
    started and no EODHD request is made.

    Phases
      1  build Release (also the analyzer gate - TreatWarningsAsErrors)
      2  focused ingestion + idempotency tests
      3  WriteGuardTests
      4  full suite
      5  build the API
      6  live: start with OperationsHost__RunCycles=true, observe the cycle, stop the API
      7  verify everything from the database

    The 4-hour watch cooldown is NOT changed. The run waits only for the next eligible tick.
#>

[CmdletBinding()]
param(
    [ValidateRange(2, 60)]
    [int]$MaxMinutes = 20,

    [ValidateRange(1, 20)]
    [int]$RequiredSuppressions = 2,

    [ValidateRange(0, 480)]
    [int]$MaxCooldownWaitMinutes = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root   = Split-Path -Parent $PSScriptRoot
$out    = Join-Path $root 'artifacts\verify'
$null   = New-Item -ItemType Directory -Force -Path $out
$stamp  = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$log    = Join-Path $out ('full-verification-' + $stamp + '.txt')
$apiOut = Join-Path $out ('full-api-' + $stamp + '.log')
$apiErr = Join-Path $out ('full-api-' + $stamp + '.err.log')

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }
function Stamp { return (Get-Date).ToUniversalTime().ToString('HH:mm:ss') + 'Z' }

function Run([string]$title, [string]$exe, [string[]]$commandArgs, [int]$tail = 0) {
    Say ''
    Say ('=== ' + $title + ' ===')
    Say ('    ' + ($commandArgs -join ' '))
    Say ''
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $code = 1
    try {
        $output = @(& $exe @commandArgs 2>&1)
        $code = $LASTEXITCODE
        $show = if ($tail -gt 0 -and $output.Count -gt $tail) { @($output | Select-Object -Last $tail) } else { $output }
        foreach ($line in $show) { Say ('    ' + [string]$line) }
    }
    catch { Say ('    RUN FAILED: ' + $_.Exception.Message); $code = 1 }
    finally { $ErrorActionPreference = $previous }
    Say ''
    Say ('    exit code: ' + $code)
    return $code
}

$WatchId  = '55dbe3e7-71ed-4602-bb34-d1ab41b3e3d0'
$BaseUrl  = 'http://localhost:5143'
$sln      = Join-Path $root 'AI-Investment-Analyst.sln'
$apiProj  = Join-Path $root 'src\AI.Investment.Api'
$apiExe   = Join-Path $root 'src\AI.Investment.Api\bin\Release\net8.0\AI.Investment.Api.exe'

$H = 'localhost'; $P = '5432'; $D = ''; $U = ''
$haveDb = $false
$psql = $null
$found = @(Get-Command psql -CommandType Application -ErrorAction SilentlyContinue)
if ($found.Count -gt 0) { $psql = [string]$found[0].Source }
if ([string]::IsNullOrWhiteSpace($psql)) {
    $c = @(Get-ChildItem -Path 'C:\Program Files\PostgreSQL' -Filter 'psql.exe' -Recurse -ErrorAction SilentlyContinue)
    if ($c.Count -gt 0) { $psql = [string]$c[0].FullName }
}

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

Say '=== IDEMPOTENCY SCOPE FIX: BUILD, TEST, LIVE ==='
Say ('UTC now : ' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
Say ('repo    : ' + $root)
Save-Log

$dotnet = $null
$d = @(Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)
if ($d.Count -gt 0) { $dotnet = [string]$d[0].Source }
if ([string]::IsNullOrWhiteSpace($dotnet)) { Say 'dotnet not found. STOPPING.'; Save-Log; exit 1 }

$null = Run 'CHANGED FILES' 'git' @('-C', $root, 'status', '--short')
$null = Run 'THE DIFF' 'git' @('-C', $root, 'diff', '--stat')

# ------------------------------------------------------------------------------------------
# A test database, so the integration tests run instead of skipping.
# ------------------------------------------------------------------------------------------
Say ''
Say '--- TEST DATABASE ---'
$dockerUp = $false
try { $null = @(& docker info 2>&1); $dockerUp = ($LASTEXITCODE -eq 0) } catch { $dockerUp = $false }

$cs = $null; $text = $null
try {
    $text = (& $dotnet user-secrets list --project $apiProj 2>&1) -join "`n"
    if ($LASTEXITCODE -eq 0) {
        foreach ($line in @($text -split "`n")) {
            $i = $line.IndexOf(' = ')
            if ($i -gt 0 -and $line.Substring(0, $i).Trim() -eq 'Database:ConnectionString') { $cs = $line.Substring($i + 3) }
        }
    }
}
catch { }
finally { $text = $null }

if (-not [string]::IsNullOrWhiteSpace($cs)) {
    $parts = @{}
    foreach ($seg in @($cs -split ';')) {
        $j = $seg.IndexOf('=')
        if ($j -gt 0) { $parts[$seg.Substring(0, $j).Trim().ToLowerInvariant()] = $seg.Substring($j + 1).Trim() }
    }
    if ($parts.ContainsKey('host'))     { $H = $parts['host'] }
    if ($parts.ContainsKey('port'))     { $P = $parts['port'] }
    if ($parts.ContainsKey('database')) { $D = $parts['database'] }
    if ($parts.ContainsKey('username')) { $U = $parts['username'] }
    if ($parts.ContainsKey('password')) { $env:PGPASSWORD = $parts['password'] }
    $haveDb = -not [string]::IsNullOrWhiteSpace($psql)

    if ($dockerUp) {
        Say '  Docker is running; Testcontainers will supply the test database.'
    }
    elseif ($haveDb) {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $created = @(& $psql -h $H -p $P -U $U -d 'postgres' -c 'CREATE DATABASE ai_investment_tests' 2>&1)
        $ErrorActionPreference = $previous
        foreach ($l in $created) { Say ('  psql: ' + ([string]$l).Trim()) }
        $rebuilt = 'Host=' + $H + ';Port=' + $P + ';Database=ai_investment_tests;Username=' + $U
        if ($parts.ContainsKey('password')) { $rebuilt = $rebuilt + ';Password=' + $parts['password'] }
        $env:AIINV_TEST_POSTGRES = $rebuilt
        Say '  AIINV_TEST_POSTGRES -> ai_investment_tests (never ai_investment; the fixture refuses it).'
    }
    $parts = $null
}
else { Say '  connection string unavailable; integration tests may skip.' }
$cs = $null
Save-Log

try {
    # --------------------------------------------------------------------------------------
    # 1-5. Build and test. Nothing live happens unless all of this is green.
    # --------------------------------------------------------------------------------------
    $buildCode = Run 'PHASE 1 - BUILD (Release, solution)' $dotnet @('build', $sln, '-c', 'Release', '--nologo') 25
    if ($buildCode -ne 0) {
        Say ''
        Say '  BUILD FAILED. Not testing, not starting anything.'
        Save-Log; Write-Host ('Written: ' + $log); exit 1
    }

    $focusedCode = Run 'PHASE 2 - FOCUSED INGESTION + IDEMPOTENCY TESTS' $dotnet @(
        'test', (Join-Path $root 'tests\AI.Investment.Application.UnitTests'), '-c', 'Release', '--no-build',
        '--filter', 'FullyQualifiedName~IngestionGatewayTests',
        '--logger', 'console;verbosity=normal', '--nologo') 60

    $guardCode = Run 'PHASE 3 - WRITEGUARDTESTS' $dotnet @(
        'test', (Join-Path $root 'tests\AI.Investment.Integration.Tests'), '-c', 'Release', '--no-build',
        '--filter', 'FullyQualifiedName~WriteGuardTests',
        '--logger', 'console;verbosity=normal', '--nologo') 40

    $suiteCode = Run 'PHASE 4 - FULL SUITE' $dotnet @('test', $sln, '-c', 'Release', '--no-build', '--nologo') 30

    $apiBuildCode = Run 'PHASE 5 - BUILD API (Release)' $dotnet @('build', $apiProj, '-c', 'Release', '--nologo') 15

    Say ''
    Say '--- TEST/BUILD SUMMARY ---'
    Say ('  build (solution) : ' + $(if ($buildCode -eq 0) { 'PASS' } else { 'FAIL' }))
    Say ('  focused ingestion: ' + $(if ($focusedCode -eq 0) { 'PASS' } else { 'FAIL' }))
    Say ('  WriteGuardTests  : ' + $(if ($guardCode -eq 0) { 'PASS' } else { 'FAIL' }))
    Say ('  full suite       : ' + $(if ($suiteCode -eq 0) { 'PASS' } else { 'FAIL' }))
    Say ('  api build        : ' + $(if ($apiBuildCode -eq 0) { 'PASS' } else { 'FAIL' }))
    Save-Log

    if ($focusedCode -ne 0 -or $guardCode -ne 0 -or $suiteCode -ne 0 -or $apiBuildCode -ne 0) {
        Say ''
        Say '  SOMETHING IS RED. The API is NOT started and no EODHD request is made.'
        Save-Log; Write-Host ('Written: ' + $log); exit 1
    }

    Remove-Item Env:\AIINV_TEST_POSTGRES -ErrorAction SilentlyContinue

    # --------------------------------------------------------------------------------------
    # 6. Live.
    # --------------------------------------------------------------------------------------
    Say ''
    Say '--- PHASE 6: LIVE ---'

    if (-not $haveDb) { Say '  no database access; cannot verify. STOPPING before starting anything.'; Save-Log; exit 1 }
    if (-not (Test-Path $apiExe)) { Say '  Release binary missing. STOPPING.'; Save-Log; exit 1 }

    $busy = @(Get-NetTCPConnection -LocalPort 5143 -State Listen -ErrorAction SilentlyContinue)
    if ($busy.Count -gt 0) { Say '  Something already listens on 5143. STOPPING.'; Save-Log; exit 1 }

    Say ('  api built : ' + (Get-Item $apiExe).LastWriteTimeUtc.ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
    Say ('  database  : ' + $D + ' on ' + $H + ':' + $P)

    $baseCycles = Sql 'select count(*) from operating_cycles'
    $baseRuns   = Sql 'select count(*) from ingestion_runs'
    $baseObs    = Sql 'select count(*) from observations'
    $baseFire   = Sql "select fire_count from watches where id = '$WatchId'"
    $baseSup    = Sql "select count(*) from audit_records where event_type = 'WatchSuppressed'"
    $baseFetch  = Sql "select count(*) from action_executions where action_type = 'ingestion.fetch'"

    Say ''
    Say '  BASELINE'
    Say ('    operating_cycles          : ' + $baseCycles)
    Say ('    ingestion_runs            : ' + $baseRuns)
    Say ('    observations              : ' + $baseObs)
    Say ('    watch fire_count          : ' + $baseFire)
    Say ('    WatchSuppressed           : ' + $baseSup)
    Say ('    ingestion.fetch executions: ' + $baseFetch)

    Say ''
    Say '  COOLDOWN NOW (expires | now | elapsed | seconds_remaining)'
    Say ('    ' + (Sql ("select last_fired_at_utc + cooldown, now(), " +
                        "(now() >= last_fired_at_utc + cooldown), " +
                        "greatest(0, ceil(extract(epoch from (last_fired_at_utc + cooldown) - now())))::bigint " +
                        "from watches where id = '$WatchId'")))
    Say '    The cooldown is NOT changed. If it has not elapsed the run waits it out below;'
    Say '    it is never shortened, and the watch is never touched.'
    Save-Log

    # The watch cannot fire while it is in cooldown, so waiting is the only honest way to see a
    # new cycle. The API is NOT started during the wait - nothing runs, and nothing is fetched.
    $remaining = Sql ("select greatest(0, ceil(extract(epoch from " +
                      "(last_fired_at_utc + cooldown) - now())))::bigint " +
                      "from watches where id = '$WatchId'")

    $waitSeconds = 0
    if ($remaining -notlike 'QUERY FAILED*' -and $remaining -ne 'NO DATABASE') { $waitSeconds = [int64]$remaining }

    if ($waitSeconds -gt ($MaxCooldownWaitMinutes * 60)) {
        Say ''
        Say ('  Cooldown has ' + [math]::Round($waitSeconds / 60.0, 1) + ' minutes left, beyond the ' +
             $MaxCooldownWaitMinutes + '-minute cap. STOPPING; nothing was started.')
        Save-Log; Write-Host ('Written: ' + $log); exit 1
    }

    if ($waitSeconds -gt 0) {
        $target = (Get-Date).AddSeconds($waitSeconds + 45)
        Say ''
        Say ('  WAITING OUT THE COOLDOWN: ' + [math]::Round($waitSeconds / 60.0, 1) + ' minutes.')
        Say '  The API is NOT started during this wait. Nothing is running, nothing is fetched.'
        Say ('  Resuming at about ' + $target.ToUniversalTime().ToString('HH:mm:ss') + 'Z')
        Save-Log

        while ((Get-Date) -lt $target) {
            Start-Sleep -Seconds 60
            $left = [math]::Round(($target - (Get-Date)).TotalMinutes, 1)
            if ($left -gt 0) { Write-Host ('    ' + (Stamp) + '  ' + $left + ' minutes left') }
        }
        Say ('  ' + (Stamp) + '  cooldown elapsed.')
        Save-Log
    }

    # Reachability first. A firing is expensive - the cooldown makes the next one four hours
    # away - so starting the API into a network that cannot reach the provider spends one for
    # nothing. This is a bare HEAD to the host with no API token and no data request; it is not
    # an ingestion and it is not the fetch under test.
    Say ''
    Say '  PROVIDER REACHABILITY (HEAD https://eodhd.com, no token, not a data request)'
    $reachable = $false
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $probe = Invoke-WebRequest -Uri 'https://eodhd.com' -Method Head -TimeoutSec 15 -UseBasicParsing
            Say ('    attempt ' + $attempt + ' : HTTP ' + [int]$probe.StatusCode)
            $reachable = $true
            break
        }
        catch {
            Say ('    attempt ' + $attempt + ' : ' + $_.Exception.GetType().Name)
            if ($attempt -lt 3) { Start-Sleep -Seconds 20 }
        }
    }

    if (-not $reachable) {
        Say ''
        Say '  eodhd.com is not reachable from this machine. The API is NOT started, so the'
        Say '  watch keeps its firing and the four-hour cooldown is not spent on a fetch that'
        Say '  would fail. Re-run when the network is back.'
        Save-Log; Write-Host ('Written: ' + $log); exit 1
    }

    $env:OperationsHost__RunCycles = 'true'
    $env:ASPNETCORE_ENVIRONMENT    = 'Development'
    $env:ASPNETCORE_URLS           = $BaseUrl

    $api = Start-Process -FilePath $apiExe -WorkingDirectory $apiProj `
        -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr `
        -PassThru -WindowStyle Hidden

    Say ''
    Say ('  API pid ' + $api.Id + ' started ' + (Stamp) + ' with RunCycles=true')
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
        Say '  /health never answered.'
        foreach ($l in @(Get-Content $apiErr -ErrorAction SilentlyContinue | Select-Object -First 40)) { Say ('    ' + $l) }
        $exitCode = 1
    }
    else {
        Say ('  healthy ' + (Stamp))
        Say ''
        Say ('  OBSERVING (max ' + $MaxMinutes + ' min)')

        $deadline = (Get-Date).AddMinutes($MaxMinutes)
        $last = ''
        $done = $false
        $sup = 0

        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 15

            $snap = Sql ("select (select count(*) from operating_cycles) || ' | ' || " +
                         "(select count(*) from ingestion_runs) || ' | ' || " +
                         "(select count(*) from action_executions where action_type = 'ingestion.fetch') || ' | ' || " +
                         "(select fire_count from watches where id = '$WatchId') || ' | ' || " +
                         "(select count(*) from audit_records where event_type = 'WatchSuppressed') || ' | ' || " +
                         "(select count(*) from operating_cycles where stopped_at_utc is null) || ' | ' || " +
                         "(select count(*) from observations)")

            if ($snap -ne $last) {
                Say ('  ' + (Stamp) + '  cycles|runs|fetches|fires|suppressed|unstopped|obs = ' + $snap)
                $last = $snap
                Save-Log
            }

            $s = Sql "select count(*) from audit_records where event_type = 'WatchSuppressed'"
            if ($s -notlike 'QUERY FAILED*' -and $baseSup -notlike 'QUERY FAILED*') { $sup = [int]$s - [int]$baseSup }

            $newFire = Sql "select fire_count from watches where id = '$WatchId'"
            $unstopped = Sql 'select count(*) from operating_cycles where stopped_at_utc is null'

            $fired = ($newFire -notlike 'QUERY FAILED*' -and [int]$newFire -gt [int]$baseFire)

            if ($fired -and $unstopped -eq '0' -and $sup -ge $RequiredSuppressions) {
                Say ('  ' + (Stamp) + '  fired, terminal, and ' + $sup + ' cooldown suppressions. Enough.')
                $done = $true
                break
            }
        }

        Say ('  ' + (Stamp) + '  observing finished. early-exit = ' + $done + ', suppressions = ' + $sup)
    }
}
catch {
    Say ''
    Say ('  UNEXPECTED: ' + $_.Exception.Message)
    $exitCode = 1
}
finally {
    Say ''
    Say '--- STOP THE API ---'
    if ($null -ne $api) {
        try {
            if (-not $api.HasExited) { Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 4 }
            Say ('  pid ' + $api.Id + ' stopped ' + (Stamp))
        }
        catch { Say ('  could not stop: ' + $_.Exception.Message) }
    }
    else { Say '  never started.' }

    $still = @(Get-NetTCPConnection -LocalPort 5143 -State Listen -ErrorAction SilentlyContinue)
    Say ('  port 5143 still listening : ' + ($still.Count -gt 0))
    $procs = @(Get-Process -Name 'AI.Investment.Api' -ErrorAction SilentlyContinue)
    Say ('  AI.Investment.Api remaining : ' + $procs.Count)

    Remove-Item Env:\OperationsHost__RunCycles -ErrorAction SilentlyContinue
    Remove-Item Env:\ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
    Remove-Item Env:\ASPNETCORE_URLS -ErrorAction SilentlyContinue
    Remove-Item Env:\AIINV_TEST_POSTGRES -ErrorAction SilentlyContinue
    Say '  RunCycles cleared.'
    Save-Log
}

# ------------------------------------------------------------------------------------------
# 7. Verify.
# ------------------------------------------------------------------------------------------
Say ''
Say '--- PHASE 7: VERIFICATION ---'

Show 'ALL CYCLES (id | status | stage | started | stopped | reason)' (
    "select id, status, stage, started_at_utc, coalesce(stopped_at_utc::text,'-'), " +
    "coalesce(left(stopped_reason, 55),'-') from operating_cycles order by started_at_utc")

Show 'DUPLICATE trigger_key (must be none)' (
    'select trigger_key, count(*) from operating_cycles group by trigger_key having count(*) > 1')

Show 'INGESTION RUNS (id | source | outcome | reason | started | completed)' (
    "select id, source_id, outcome, coalesce(left(reason,45),'-'), started_at_utc, " +
    "coalesce(completed_at_utc::text,'-') from ingestion_runs order by started_at_utc")

Show 'THE OWNED GRAPH (source | category | region | subject_kind | subject_id | correlation)' (
    'select source_id, category, region, subject_kind, subject_identifier, correlation_id ' +
    'from ingestion_runs order by started_at_utc')

Show 'ARTIFACTS PER RUN (id | json length | content)' (
    'select id, length(artifacts::text), left(artifacts::text, 90) from ingestion_runs order by started_at_utc')

Show 'IDEMPOTENCY CLAIMS for ingestion (key | claimed)' (
    "select idempotency_key, claimed_at_utc from processed_actions " +
    "where length(idempotency_key) > 60 order by claimed_at_utc")

Show 'ingestion.fetch EXECUTIONS (started | completed | status)' (
    "select started_at_utc, coalesce(completed_at_utc::text,'-'), status from action_executions " +
    "where action_type = 'ingestion.fetch' order by started_at_utc")

Show 'AUDIT BY EVENT TYPE (event_type | count | newest)' (
    'select event_type, count(*), max(occurred_at_utc) from audit_records ' +
    'group by event_type order by max(occurred_at_utc) desc limit 14')

Show 'DuplicateSuppressed audits (must NOT include a new ingestion one)' (
    "select occurred_at_utc, coalesce(action_type,'-') from audit_records " +
    "where event_type = 'DuplicateSuppressed' order by occurred_at_utc desc limit 5")

Show 'OBSERVATIONS (count | newest)' 'select count(*), max(retrieved_at_utc) from observations'

Show 'OBSERVATIONS sample (subject | attribute | value | source)' (
    'select subject_identifier, attribute, value, source_id from observations ' +
    'order by retrieved_at_utc desc limit 10')

Show 'ESCALATIONS (raised | reason | cycle)' (
    "select raised_at_utc, left(reason, 55), coalesce(cycle_id::text,'-') from escalations " +
    'order by raised_at_utc desc limit 6')

Show 'OUTBOX (status | count)' 'select status, count(*) from outbox_messages group by status'

Show 'COOLDOWN SUPPRESSIONS (newest 5)' (
    "select occurred_at_utc, left(summary, 100) from audit_records " +
    "where event_type = 'WatchSuppressed' order by occurred_at_utc desc limit 5")

Show 'THE WATCH, AFTER (enabled | fire_count | last_fired | cooldown | interval)' (
    'select enabled, fire_count, last_fired_at_utc, cooldown::text, condition_interval::text ' +
    "from watches where id = '$WatchId'")

Say ''
Say '--- API LOG CHECKS ---'

Say ''
Say '  UNAUTHORISED WRITE CHECK'
$bad = @()
foreach ($f in @($apiOut, $apiErr)) {
    if (Test-Path $f) { $bad += @(Select-String -Path $f -Pattern 'UnauthorizedWriteException' -SimpleMatch -ErrorAction SilentlyContinue) }
}
if ($bad.Count -eq 0) { Say '    PASS  none.' }
else { Say ('    FAIL  ' + $bad.Count); $exitCode = 1 }

Say ''
Say '  REAL EODHD HTTP REQUEST EVIDENCE (HttpClient / provider lines)'
$http = @()
if (Test-Path $apiOut) {
    $http = @(Select-String -Path $apiOut -Pattern 'eodhd\.com|Start processing HTTP|Sending HTTP|Received HTTP|End processing HTTP' `
        -ErrorAction SilentlyContinue | Select-Object -First 25)
}
if ($http.Count -eq 0) { Say '    (no HTTP client lines matched)' }
foreach ($m in $http) { Say ('    ' + $m.Line.Trim()) }

Say ''
Say '  SCHEDULE / CYCLE SUMMARY LINES'
$sum = @()
if (Test-Path $apiOut) {
    $sum = @(Select-String -Path $apiOut -Pattern 'Schedule pass:|Cycle .* advanced|advanced to|Operating cycles enabled' `
        -ErrorAction SilentlyContinue | Select-Object -Last 25)
}
foreach ($m in $sum) { Say ('    ' + $m.Line.Trim()) }

Say ''
Say '  STDERR'
$errs = @()
if (Test-Path $apiErr) { $errs = @(Get-Content $apiErr -ErrorAction SilentlyContinue | Select-Object -First 25) }
if ($errs.Count -eq 0) { Say '    (empty)' }
foreach ($l in $errs) { Say ('    ' + $l) }

Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue

Say ''
Say '=== END ==='
Say ('  API stopped. RunCycles cleared. Transcript: ' + $log)

Save-Log
Write-Host ''
Write-Host ('Written: ' + $log)
exit $exitCode
