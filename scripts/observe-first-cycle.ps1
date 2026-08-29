#requires -Version 5.1
<#
    OBSERVE THE FIRST CYCLE. READ ONLY.

    Safe to run as often as you like, before, during and after the observation window.
    It reads; it never writes. No configuration, no database row, no watch, no cycle,
    no EODHD request. Nothing here can start anything.

    Run it once before enabling RunCycles to have a baseline, then every few minutes
    while the window is open. Every section answers one question about the first cycle.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$log  = Join-Path $out ("observe-" + $stamp + ".txt")

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }

function Get-BodyText($response) {
    if ($null -eq $response) { return '' }
    $content = $response.Content
    if ($null -eq $content) { return '' }
    if ($content -is [string]) { return $content.Trim() }
    if ($content -is [byte[]]) { return ([Text.Encoding]::UTF8.GetString($content)).Trim() }
    return (($content | Out-String).Trim())
}

$BaseUrl    = 'http://localhost:5143'
$SourceId   = 'eodhd-eod'
$Symbol     = 'AAPL.US'
$apiProject = Join-Path $root 'src\AI.Investment.Api'

Say '=== OBSERVING THE FIRST CYCLE (READ ONLY) ==='
Say ("UTC now : " + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
Say ''

# --------------------------------------------------------------------------------------
Say '--- API ---'
foreach ($probe in @('/health/live', '/health/ready')) {
    try {
        $r = Invoke-WebRequest -Uri ($BaseUrl + $probe) -UseBasicParsing -TimeoutSec 15
        Say ('  ' + $probe.PadRight(14) + ' ' + [int]$r.StatusCode + ' ' + (Get-BodyText $r))
    }
    catch { Say ('  ' + $probe.PadRight(14) + ' FAILED ' + $_.Exception.Message) }
}

# --------------------------------------------------------------------------------------
# Database. Headers are printed rather than assumed, so this does not depend on knowing
# every column name - and a schema that changes shows up rather than breaking a query.
# --------------------------------------------------------------------------------------
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

$script:Pg = $false
$script:H='localhost'; $script:P='5432'; $script:D=''; $script:U=''

if ($null -ne $psql) {
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

    if (-not [string]::IsNullOrWhiteSpace($cs)) {
        $parts = @{}
        foreach ($seg in @($cs -split ';')) {
            $j = $seg.IndexOf('=')
            if ($j -gt 0) { $parts[$seg.Substring(0,$j).Trim().ToLowerInvariant()] = $seg.Substring($j+1).Trim() }
        }
        $cs = $null
        if ($parts.ContainsKey('host'))     { $script:H = $parts['host'] }
        if ($parts.ContainsKey('port'))     { $script:P = $parts['port'] }
        if ($parts.ContainsKey('database')) { $script:D = $parts['database'] }
        if ($parts.ContainsKey('username')) { $script:U = $parts['username'] }
        if ($parts.ContainsKey('password')) { $env:PGPASSWORD = $parts['password'] }
        $parts = $null
        $script:Pg = $true
    }
}

<#
    -t omitted on purpose: the header row names the columns.

    psql writes its diagnostics to stderr, and a native command's stderr becomes an
    ErrorRecord that ErrorActionPreference = Stop treats as TERMINATING. That is what
    ended the run on the missing table: not the missing table itself, but a failed query
    being fatal instead of reportable. An observer must be able to report a failure.
#>
function Sql([string]$sql, [switch]$Quiet) {
    if (-not $script:Pg) { return @('NO DATABASE') }

    $arguments = @('-h', $script:H, '-p', $script:P, '-U', $script:U, '-d', $script:D, '-A', '-F', ' | ', '-c', $sql)
    if ($Quiet) { $arguments = @('-t') + $arguments }

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $raw = $null
    $code = 0
    try {
        $raw = & $psql @arguments 2>&1
        $code = $LASTEXITCODE
    }
    catch {
        return @('QUERY FAILED: ' + $_.Exception.Message)
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($code -ne 0) {
        $detail = @(($raw | Out-String) -split "`n" |
                    Where-Object { $_ -match 'ERROR' } | Select-Object -First 1)
        $message = if ($detail.Count -gt 0) { $detail[0].Trim() } else { 'query failed' }
        return @('QUERY FAILED: ' + $message)
    }

    return @(($raw | Out-String) -split "`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

<#
    Whether a relation exists, asked of the catalogue rather than discovered by crashing
    into it. to_regclass returns null instead of raising when the name is unknown.
#>
function Test-Relation([string]$name) {
    # Quote the identifier. PostgreSQL folds an UNQUOTED name to lower case, so asking
    # to_regclass('public.__EFMigrationsHistory') looks for __efmigrationshistory and
    # answers "no" whether or not the table is there. Every table this project defines is
    # lower-case snake_case except EF Core's own history table - which is exactly the one
    # this check got wrong, and reported as missing when it was not.
    $quoted = '"' + $name.Replace('"', '""') + '"'
    $answer = (Sql ("select to_regclass('public." + $quoted + "') is not null") -Quiet) -join ''

    return ($answer.Trim() -eq 't')
}

function Section([string]$title, [string]$sql, [string]$relation = '') {
    Say ''
    Say ('--- ' + $title + ' ---')

    if ($relation -ne '' -and -not (Test-Relation $relation)) {
        Say ('  NOT PRESENT - relation "' + $relation + '" does not exist in this database.')
        return
    }

    foreach ($row in (Sql $sql)) { Say ('  ' + $row.TrimEnd()) }
}

try {
    Say ''
    Say ('--- DATABASE ' + $script:H + ':' + $script:P + '/' + $script:D + ' (password not shown) ---')

    # 1. Has the watch fired? fire_count and last_fired_at_utc are the schedule's own record.
    Section 'WATCH' ("select name, enabled, fire_count, created_at_utc, last_fired_at_utc, " +
                     "condition_interval::text as interval, max_signal_age::text as max_age " +
                     "from watches where target_identifier = '$Symbol'") 'watches'

    # 2. Did a cycle start, and where did it get to?
    Section 'OPERATING CYCLES (newest 5)' `
        'select * from operating_cycles order by started_at_utc desc limit 5' 'operating_cycles'

    # 3. Did the fetch happen, and what did the gateway decide? A refused run is recorded
    #    with the rule that refused it.
    Section 'INGESTION RUNS for eodhd-eod (newest 5)' `
        ("select started_at_utc, completed_at_utc, outcome, refusal_rule_id, left(coalesce(reason,''), 90) as reason " +
         "from ingestion_runs where source_id = '$SourceId' order by started_at_utc desc limit 5") 'ingestion_runs'

    # 4. Did normalisation store anything?
    Section 'OBSERVATIONS (count, and newest 3)' `
        'select count(*) as total from observations'
    Section 'OBSERVATIONS sample' `
        'select * from observations order by 1 desc limit 3' 'observations' 'observations'

    # 5. Did the pass propose a candidate?
    Section 'OPPORTUNITIES (newest 5)' `
        'select * from opportunities order by 1 desc limit 5' 'opportunities'

    # 6. Anything asking for a person? The provider-failure escalation lands here.
    Section 'ESCALATIONS (newest 5)' `
        ('select raised_at_utc, reason, capability, resolved_at_utc, ' +
         "left(explanation, 110) as explanation from escalations order by raised_at_utc desc limit 5") 'escalations'

    # 7. The seam's own account of what happened.
    Section 'AUDIT (newest 12)' `
        ("select occurred_at_utc, event_type, actor, coalesce(action_type,'') as action_type, " +
         "coalesce(outcome::text,'') as outcome, left(summary, 70) as summary " +
         'from audit_records order by occurred_at_utc desc limit 12') 'audit_records'

    Say ''
    Say '--- COUNTS ---'

    # relation, label, query
    foreach ($item in @(
        @('operating_cycles', 'operating_cycles',            'select count(*) from operating_cycles'),
        @('ingestion_runs',   'ingestion_runs (eodhd-eod)',  "select count(*) from ingestion_runs where source_id = '$SourceId'"),
        @('observations',     'observations',                'select count(*) from observations'),
        @('opportunities',    'opportunities',               'select count(*) from opportunities'),
        @('escalations',      'escalations outstanding',     'select count(*) from escalations where resolved_at_utc is null'),
        @('ledger_entries',   'ledger_entries',              'select count(*) from ledger_entries'),
        @('position_events',  'position_events',             'select count(*) from position_events'))) {

        if (-not (Test-Relation $item[0])) {
            Say ('  ' + $item[1].PadRight(28) + ' : NOT PRESENT (relation does not exist)')
            continue
        }

        Say ('  ' + $item[1].PadRight(28) + ' : ' + ((Sql $item[2] -Quiet) -join ''))
    }

    Say ''
    Say '  position_events is where a fill would move a holding, and it MUST stay 0 -'
    Say '  or be absent. A price review records an opportunity; it places no order.'
    Say '  There is no other position store to check instead: Position is not persisted,'
    Say '  it is replayed from these events by PositionCalculator. ledger_entries is the'
    Say '  capital ledger, which is money rather than holdings.'

    # Which migrations this database has, so an absent relation has an explanation rather
    # than being a mystery. Read-only; the table is EF Core's own.
    Say ''
    Say '--- APPLIED MIGRATIONS ---'

    if (Test-Relation '__EFMigrationsHistory') {
        foreach ($row in (Sql 'select "MigrationId" from "__EFMigrationsHistory" order by "MigrationId"' -Quiet)) {
            Say ('  ' + $row.Trim())
        }
    }
    else {
        Say '  NOT PRESENT - this database has no EF Core migrations history table.'
    }
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

# --------------------------------------------------------------------------------------
Say ''
Say '--- RAW ARCHIVE ---'
try {
    $archive = Join-Path $apiProject 'archive'
    if (Test-Path -LiteralPath $archive -PathType Container) {
        $files = @(Get-ChildItem -LiteralPath $archive -Recurse -File -ErrorAction SilentlyContinue)
        Say ('  payload files : ' + $files.Count)
        foreach ($f in @($files | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 3)) {
            Say ('    ' + $f.LastWriteTimeUtc.ToString('yyyy-MM-dd HH:mm:ss') + 'Z  ' +
                 $f.Length.ToString() + ' bytes  ' + $f.Name)
        }
    }
    else {
        Say '  archive folder does not exist yet (created on the first archived payload).'
    }
}
catch { Say ('  FAILED : ' + $_.Exception.Message) }

Say ''
Say '=== END. NOTHING WAS CHANGED. ==='

Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8
Write-Host ''
Write-Host ('Written: ' + $log)
