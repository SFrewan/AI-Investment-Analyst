#requires -Version 5.1
<#
    PIPELINE VERIFICATION, SEPARATED FROM COOLDOWN VERIFICATION.

    Phase 1  Release build of the whole solution.
    Phase 2  The focused tests: the two cooldown proofs, and the three regression tests
             behind the write guard, the idempotency scope and the owned subject.
    Phase 3  The full suite. The live smoke test reports as SKIPPED here, which is the
             gate working.
    Phase 4  Reachability pre-flight against the provider host. No token, no data.
    Phase 5  ONE live cycle, driven through the isolated verification path.
    Phase 6  Read-only corroboration from the database.

    Phases 4-6 run only if 1-3 all passed. Nothing is left running.
#>

[CmdletBinding()]
param(
    [switch]$SkipFullSuite
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log = Join-Path $out 'verify-pipeline.txt'

$lines = New-Object 'System.Collections.Generic.List[string]'

function Say([string]$text) {
    $null = $lines.Add($text)
    Write-Host $text
}

function Save-Log {
    Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8
}

function Stamp {
    return ((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
}

function Say-Header([string]$title) {
    Say ''
    Say '---------------------------------------------------------------'
    Say ('  ' + $title + '   [' + (Stamp) + ']')
    Say '---------------------------------------------------------------'
    Save-Log
}

# ---------------------------------------------------------------- dotnet ----

function Invoke-Dotnet([string]$label, [string[]]$dotnetArgs, [string]$logName) {
    $file = Join-Path $out $logName
    Say ('  running: dotnet ' + ($dotnetArgs -join ' '))

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $code = 0
    try {
        & dotnet @dotnetArgs 2>&1 | Tee-Object -FilePath $file | Out-Null
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    $tail = @()
    if (Test-Path $file) {
        $tail = @(Get-Content -Path $file -Tail 25)
    }

    if ($code -eq 0) {
        Say ('  PASS  ' + $label)
    }
    else {
        Say ('  FAIL  ' + $label + '  (exit ' + [string]$code + ')')
    }

    foreach ($t in $tail) {
        if ($t -match 'error |Failed!|Passed!|Passed:|Failed:|Skipped:|Build succeeded|Build FAILED|warning ') {
            Say ('        ' + $t.Trim())
        }
    }

    # An all-skipped run exits 0. Reporting that as a pass is how a regression proof stops
    # proving anything without anybody noticing, so it is called out and it fails the phase.
    $allSkipped = $false
    foreach ($t in $tail) {
        if ($t -match 'Skipped!\s+- Failed:\s+0, Passed:\s+0, Skipped:\s+[1-9]') { $allSkipped = $true }
    }

    if ($allSkipped -and $code -eq 0) {
        Say '  NOT PROVED  every test in this phase was SKIPPED, not run.'
        $code = 3
    }

    Say ('        full output: ' + $file)
    Save-Log

    return [pscustomobject]@{ Label = $label; Code = $code; Log = $file }
}

# -------------------------------------------------------------- database ----

$psql = $null
$found = @(Get-Command psql -CommandType Application -ErrorAction SilentlyContinue)
if ($found.Count -gt 0) { $psql = [string]$found[0].Source }
if ([string]::IsNullOrWhiteSpace($psql)) {
    $c = @(Get-ChildItem -Path 'C:\Program Files\PostgreSQL' -Filter 'psql.exe' -Recurse -ErrorAction SilentlyContinue)
    if ($c.Count -gt 0) { $psql = [string]$c[0].FullName }
}

$H = 'localhost'; $P = '5432'; $D = ''; $U = ''
$haveDb = $false

function Connect-Database {
    if ([string]::IsNullOrWhiteSpace($script:psql)) { return $false }

    $apiProject = Join-Path $script:root 'src\AI.Investment.Api'
    $cs = $null
    $text = $null
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

    if ([string]::IsNullOrWhiteSpace($cs)) { return $false }

    $parts = @{}
    foreach ($seg in @($cs -split ';')) {
        $j = $seg.IndexOf('=')
        if ($j -gt 0) { $parts[$seg.Substring(0, $j).Trim().ToLowerInvariant()] = $seg.Substring($j + 1).Trim() }
    }
    $cs = $null

    if ($parts.ContainsKey('host')) { $script:H = $parts['host'] }
    if ($parts.ContainsKey('port')) { $script:P = $parts['port'] }
    if ($parts.ContainsKey('database')) { $script:D = $parts['database'] }
    if ($parts.ContainsKey('username')) { $script:U = $parts['username'] }
    if ($parts.ContainsKey('password')) { $env:PGPASSWORD = $parts['password'] }
    $parts = $null

    # Server-enforced. Every statement below is a SELECT, and the server refuses anything else.
    $env:PGOPTIONS = '-c default_transaction_read_only=on'
    $script:haveDb = $true

    return $true
}

function Sql([string]$sql) {
    if (-not $script:haveDb) { return 'NO DATABASE' }

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $raw = $null
    $code = 0
    try {
        $raw = & $script:psql -h $script:H -p $script:P -U $script:U -d $script:D -t -A -F ' | ' -c $sql 2>&1
        $code = $LASTEXITCODE
    }
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
    Save-Log
}

# ============================================================ phases ========

Say '==============================================================='
Say ' PIPELINE VERIFICATION (cooldown untouched)'
Say (' started : ' + (Stamp))
Say (' repo    : ' + $root)
Say '==============================================================='

$results = New-Object 'System.Collections.Generic.List[object]'

# The integration fixture needs a database whose name ends in '_tests'. Same two sources as
# scripts\verify.ps1: an environment variable if one is already set, otherwise the git-ignored
# scripts\verify.local.ps1. Nothing from that file is printed.
$localSettings = Join-Path $PSScriptRoot 'verify.local.ps1'

if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES) -and (Test-Path -Path $localSettings)) {
    . $localSettings
}

if ([string]::IsNullOrWhiteSpace($env:AIINV_TEST_POSTGRES)) {
    Say ''
    Say '  WARNING: AIINV_TEST_POSTGRES is not set and scripts\verify.local.ps1 is absent.'
    Say '           Every integration test that needs a database will SKIP, and a skipped'
    Say '           regression proof is not a passing one. See scripts\verify.local.example.ps1.'
}
else {
    Say ''
    Say '  Integration database configured (value not printed).'
}

# ---- Phase 1: build --------------------------------------------------------

Say-Header 'PHASE 1 - Release build'

$build = Invoke-Dotnet 'Release build' @(
    'build'
    (Join-Path $root 'AI-Investment-Analyst.sln')
    '-c'
    'Release'
    '--nologo'
) 'pipeline-build.log'
$null = $results.Add($build)

# ---- Phase 2: focused tests ------------------------------------------------

Say-Header 'PHASE 2 - focused tests'

Say '  The cooldown proofs stay exactly where they were. They are named here so it is'
Say '  visible that the isolated path did not replace them.'
Say ''

$focusedFilter = @(
    'FullyQualifiedName~A_watch_inside_its_cooldown_does_not_fire_again'
    'FullyQualifiedName~A_second_observation_inside_the_cooldown_is_suppressed_and_recorded'
    'FullyQualifiedName~Recording_a_firing_starts_the_cooldown_and_counts'
    'FullyQualifiedName~Two_ingestions_for_the_same_correlation_fetch_once_and_suppress_the_second'
    'FullyQualifiedName~A_new_correlation_may_fetch_an_identical_request_again'
    'FullyQualifiedName~Every_observation_gets_its_own_subject_instance'
) -join '|'

$focusedDomain = Invoke-Dotnet 'focused: domain + application' @(
    'test'
    (Join-Path $root 'AI-Investment-Analyst.sln')
    '-c'
    'Release'
    '--no-build'
    '--nologo'
    '--filter'
    $focusedFilter
) 'pipeline-focused.log'
$null = $results.Add($focusedDomain)

$guardFilter = @(
    'FullyQualifiedName~WriteGuardTests'
    'FullyQualifiedName~ObservationPersistenceTests'
) -join '|'

$focusedGuard = Invoke-Dotnet 'focused: write guard + owned subject (Postgres)' @(
    'test'
    (Join-Path $root 'tests\AI.Investment.Integration.Tests')
    '-c'
    'Release'
    '--no-build'
    '--nologo'
    '--filter'
    $guardFilter
) 'pipeline-focused-guard.log'
$null = $results.Add($focusedGuard)

# ---- Phase 3: full suite ---------------------------------------------------

if ($SkipFullSuite) {
    Say-Header 'PHASE 3 - full suite (SKIPPED by switch)'
}
else {
    Say-Header 'PHASE 3 - full suite'
    Say '  The live smoke test must report as SKIPPED in this phase. That is the gate.'
    Say ''

    $full = Invoke-Dotnet 'full suite' @(
        'test'
        (Join-Path $root 'AI-Investment-Analyst.sln')
        '-c'
        'Release'
        '--no-build'
        '--nologo'
    ) 'pipeline-full.log'
    $null = $results.Add($full)
}

$failed = @($results | Where-Object { $_.Code -ne 0 })

if ($failed.Count -gt 0) {
    Say ''
    Say '==============================================================='
    Say ' STOPPING BEFORE THE LIVE PHASE. Tests must pass first.'
    foreach ($f in $failed) { Say ('   FAILED: ' + $f.Label + '  -> ' + $f.Log) }
    Say '==============================================================='
    Save-Log
    exit 1
}

# ---- Phase 4: reachability -------------------------------------------------

Say-Header 'PHASE 4 - provider reachability pre-flight'

Say '  A bare HEAD to the provider host. No token, no query string, no data.'
Say '  A failed fetch inside the cycle would otherwise look like a code defect.'

$reachable = $false
try {
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    $probe = Invoke-WebRequest -Uri 'https://eodhd.com' -Method Head -TimeoutSec 20 -UseBasicParsing
    Say ('  reachable, status ' + [string]$probe.StatusCode)
    $reachable = $true
}
catch {
    Say ('  NOT reachable: ' + $_.Exception.Message)
}

Save-Log

if (-not $reachable) {
    Say ''
    Say ' STOPPING. The provider host is not reachable from here, so a failed cycle'
    Say ' would prove nothing about the pipeline. Re-run when the network is back.'
    Save-Log
    exit 2
}

# ---- Phase 5: one live cycle ----------------------------------------------

Say-Header 'PHASE 5 - ONE live cycle through the isolated path'

Say '  Watch.Evaluate is never called and RecordFiring never runs, so the 4-hour'
Say '  cooldown is untouched. The trigger key carries the current UTC hour, so a'
Say '  re-run inside this hour resumes that cycle rather than buying another.'
Say ''

$live = $null
$env:AIINV_LIVE_SMOKE = '1'
try {
    $live = Invoke-Dotnet 'live end-to-end cycle' @(
        'test'
        (Join-Path $root 'tests\AI.Investment.Api.Tests')
        '-c'
        'Release'
        '--no-build'
        '--nologo'
        '--filter'
        'FullyQualifiedName~LiveCycleSmokeTests'
    ) 'pipeline-live.log'
}
finally {
    Remove-Item Env:\AIINV_LIVE_SMOKE -ErrorAction SilentlyContinue
}

if ($null -ne $live) { $null = $results.Add($live) }

# ---- Phase 6: database corroboration --------------------------------------

Say-Header 'PHASE 6 - read-only corroboration'

if (-not (Connect-Database)) {
    Say '  Database unavailable (psql or the connection string could not be resolved).'
    Say '  The test assertions above are the record; this phase only corroborates them.'
}
else {
    Show 'read-only session proof' 'show default_transaction_read_only'

    # Column names are discovered rather than assumed. Three of the queries below were written
    # against remembered names, and a wrong one would otherwise read as an empty result.
    Show 'tables in this database' (
        "select table_name from information_schema.tables " +
        "where table_schema = 'public' order by table_name")

    Show 'columns of the tables this phase reads' (
        "select table_name, string_agg(column_name, ' ') " +
        "from information_schema.columns where table_schema = 'public' " +
        "and table_name in ('operating_cycles', 'watches', 'ingestion_runs', " +
        "'observations', 'audit_records', 'escalations') " +
        "group by table_name order by table_name")

    Show 'the isolated cycles this hour (status must be Completed, stage 14)' (
        'select id, status, stage, trigger_key, correlation_id, started_at_utc, ' +
        'stopped_at_utc, escalation_count from operating_cycles ' +
        "where trigger_key like 'live-smoke:%' order by started_at_utc desc limit 5")

    Show 'the ingestion run that cycle made' (
        'select id, source_id, outcome, coalesce(refusal_rule_id, ' + "'-'), " +
        'correlation_id, started_at_utc, completed_at_utc from ingestion_runs ' +
        "where correlation_id like 'cycle-%' order by started_at_utc desc limit 5")

    Show 'observations persisted, newest first - every row must carry its subject' (
        'select subject_kind, subject_identifier, attribute, count(*) ' +
        'from observations group by 1,2,3 order by 4 desc limit 10')

    Show 'null subjects anywhere (must be zero)' (
        'select count(*) from observations ' +
        'where subject_kind is null or subject_identifier is null')

    Show 'THE COOLDOWN, UNCHANGED (fire_count and last_fired_at_utc must not have moved)' (
        'select id, name, target_kind, target_identifier, enabled, fire_count, ' +
        'last_fired_at_utc, cooldown from watches order by name')

    Show 'audit trail for the isolated cycles' (
        'select occurred_at_utc, event_type, left(summary, 90) from audit_records ' +
        "where correlation_id like 'live-smoke-%' or correlation_id like 'cycle-%' " +
        'order by occurred_at_utc desc limit 15')

    Show 'escalations raised (an empty result is the good outcome)' (
        'select raised_at_utc, reason, cycle_id, left(explanation, 80) from escalations ' +
        'order by raised_at_utc desc limit 5')

    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:\PGOPTIONS -ErrorAction SilentlyContinue
}

# ---- verdict ---------------------------------------------------------------

Say ''
Say '==============================================================='
Say ' VERDICT'
foreach ($r in $results) {
    $mark = '  PASS  '
    if ($r.Code -ne 0) { $mark = '  FAIL  ' }
    Say ($mark + $r.Label)
}
Say ('  finished: ' + (Stamp))
Say '==============================================================='
Save-Log

Write-Host ''
Write-Host ('Written: ' + $log)

$stillFailed = @($results | Where-Object { $_.Code -ne 0 })
if ($stillFailed.Count -gt 0) { exit 1 }
exit 0
