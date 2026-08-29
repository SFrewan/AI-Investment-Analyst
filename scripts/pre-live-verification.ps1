#requires -Version 5.1
<#
    PRE-LIVE VERIFICATION. READ ONLY.

    Changes nothing: no code, no configuration, no User Secrets, no watch, no cycle,
    no RunCycles change, no EODHD request.

    The single exception, and it is the same one you already accepted: check 7 creates
    one zero-byte probe file with an obvious name and deletes it again, because that is
    the only reliable way to know a directory is writable. No archive payload, no
    database row, no configuration.

    DISCLOSURE RULE - UNCHANGED
    Check 2 proves what the RUNNING API loaded, which the secrets file cannot: only the
    API can say which privileges its principal actually carries. That needs one
    authenticated call, so the key is read with Read-Host -AsSecureString, used for one
    GET, and zeroed. It is never printed, logged, hashed or measured. Skip the prompt
    (press Enter) and the check falls back to the configured shape, which is weaker
    evidence and is labelled as such.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log  = Join-Path $out 'pre-live-verification.txt'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }

function Get-Prop($obj, [string]$name, $fallback = $null) {
    if ($null -eq $obj) { return $fallback }
    $p = $obj.PSObject.Properties[$name]
    if ($null -eq $p) { return $fallback }
    if ($null -eq $p.Value) { return $fallback }
    return $p.Value
}

function Get-First($response) {
    if ($null -eq $response) { return $null }
    $items = @($response)
    if ($items.Count -eq 0) { return $null }
    return $items[0]
}

function Get-BoolOrNull($obj, [string]$name) {
    $v = Get-Prop $obj $name $null
    if ($null -eq $v) { return $null }
    if ($v -is [bool]) { return $v }
    $s = [string]$v
    if ($s -eq 'True' -or $s -eq 'true') { return $true }
    if ($s -eq 'False' -or $s -eq 'false') { return $false }
    return $null
}

<#
    PostgreSQL prints an interval column as "1 day", "1 day 06:00:00" or "04:00:00".
    TimeSpan.Parse accepts only the last of those, so the query below asks for seconds
    as well and this is the fallback for a store that is not an interval type.
#>
function ConvertTo-TimeSpanFlexible([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }

    $t = $text.Trim()
    $days = 0

    if ($t -match '^\s*(-?\d+)\s+days?\s*(.*)$') {
        $days = [int]$Matches[1]
        $t = $Matches[2].Trim()
    }

    $clock = [TimeSpan]::Zero
    if (-not [string]::IsNullOrWhiteSpace($t)) {
        $parsed = [TimeSpan]::Zero
        if (-not [TimeSpan]::TryParse($t, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
            return $null
        }
        $clock = $parsed
    }

    return ([TimeSpan]::FromDays($days) + $clock)
}

# A timestamptz comes back with an offset and a plain timestamp without one. Both must end
# up as UTC; assuming local for the second would silently shift every boundary.
function ConvertTo-Utc([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }

    $styles = [Globalization.DateTimeStyles]::AdjustToUniversal -bor
              [Globalization.DateTimeStyles]::AssumeUniversal
    $parsed = [DateTime]::MinValue

    if (-not [DateTime]::TryParse($text.Trim(), [Globalization.CultureInfo]::InvariantCulture, $styles, [ref]$parsed)) {
        return $null
    }

    return [DateTime]::SpecifyKind($parsed, 'Utc')
}

<#
    Invoke-WebRequest returns Content as a string in Windows PowerShell 5.1, and in
    PowerShell 7 only when the response declares a textual content type - otherwise a
    byte array, which Out-String renders one number per line. Decode either.
#>
function Get-BodyText($response) {
    if ($null -eq $response) { return '' }

    $content = $response.Content

    if ($null -eq $content) { return '' }
    if ($content -is [string]) { return $content.Trim() }
    if ($content -is [byte[]]) { return ([Text.Encoding]::UTF8.GetString($content)).Trim() }

    return (($content | Out-String).Trim())
}

function Show([object]$value, [string]$whenNull = '(not stated)') {
    if ($null -eq $value) { return $whenNull }
    return [string]$value
}

$BaseUrl    = 'http://localhost:5143'
$SourceId   = 'eodhd-eod'
$Symbol     = 'AAPL.US'
$Template   = 'equity-price-review'
$apiProject = Join-Path $root 'src\AI.Investment.Api'

$script:BoundaryDone = $false
$verdicts = New-Object 'System.Collections.Generic.List[string]'
function Verdict([int]$n, [string]$name, $ok, [string]$detail = '') {
    $mark = if ($ok -eq $true) { 'PASS' } elseif ($ok -eq $false) { 'FAIL' } else { 'UNKNOWN' }
    $null = $verdicts.Add(('  {0}. {1,-52} {2}  {3}' -f $n, $name, $mark, $detail).TrimEnd())
}

Say '=== PRE-LIVE VERIFICATION (READ ONLY) ==='
Say ("timestamp (local) : " + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
Say ("timestamp (UTC)   : " + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
Say ("powershell        : " + $PSVersionTable.PSVersion.ToString())
Say ''

# --------------------------------------------------------------------------------------
# 1. API health
# --------------------------------------------------------------------------------------
Say '--- 1. API RUNNING AND HEALTHY ---'

$live = $false
$ready = $false

foreach ($probe in @('/health/live', '/health/ready')) {
    try {
        $r = Invoke-WebRequest -Uri ($BaseUrl + $probe) -UseBasicParsing -TimeoutSec 15
        $body = Get-BodyText $r
        Say ('  GET ' + $probe.PadRight(14) + ' -> ' + [int]$r.StatusCode + ' ' + $body)
        if ($probe -eq '/health/live'  -and $body -match 'Healthy') { $live = $true }
        if ($probe -eq '/health/ready' -and $body -match 'Healthy') { $ready = $true }
    }
    catch {
        Say ('  GET ' + $probe.PadRight(14) + ' -> FAILED ' + $_.Exception.Message)
    }
}

Say ('  /health/ready runs the postgresql check, so Healthy there also proves the database.')
Verdict 1 'API running and healthy' ($live -and $ready) ("live=$live ready=$ready")

# --------------------------------------------------------------------------------------
# 3. Source active   (checked before 2, because 2 may prompt)
# --------------------------------------------------------------------------------------
Say ''
Say '--- 3. SOURCE eodhd-eod IS ACTIVE ---'

$sourceActive = $null
try {
    $source = Get-First (Invoke-RestMethod -Uri ($BaseUrl + '/api/sources/' + $SourceId) -TimeoutSec 30)
    $sourceActive = Get-BoolOrNull $source 'isActive'
    Say ('  id       : ' + (Show (Get-Prop $source 'id')))
    Say ('  isActive : ' + (Show $sourceActive))
    Say ('  updated  : ' + (Show (Get-Prop $source 'updatedAtUtc')))
}
catch {
    Say ('  FAILED : ' + $_.Exception.Message)
}

Verdict 3 'eodhd-eod is active' ($sourceActive -eq $true)

# --------------------------------------------------------------------------------------
# 5. RunCycles
# --------------------------------------------------------------------------------------
Say ''
Say '--- 5. OperationsHost:RunCycles IS FALSE ---'

$devSettings = Join-Path $apiProject 'appsettings.Development.json'
$runCycles = $null
try {
    if (Test-Path -LiteralPath $devSettings) {
        $json = Get-Content -LiteralPath $devSettings -Raw | ConvertFrom-Json
        $runCycles = Get-BoolOrNull (Get-Prop $json 'OperationsHost' $null) 'RunCycles'
    }
}
catch { $runCycles = $null }

Say ('  appsettings.Development.json : ' + (Show $runCycles))
Say '  NOTE: this is the file. The running process read it at startup; a cycle host that'
Say '  is off logs "Operating cycles are not advanced on this instance" once at boot.'
Verdict 5 'RunCycles is false' ($runCycles -eq $false)

# --------------------------------------------------------------------------------------
# Database-backed checks: 4, 6, 8, 9
# --------------------------------------------------------------------------------------
Say ''
Say '--- DATABASE ---'

$psql = $null
try {
    $cmd = @(Get-Command psql -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
    if ($null -ne $cmd) { $psql = [string]$cmd.Source }
    if ([string]::IsNullOrWhiteSpace($psql)) {
        $candidates = @(Get-ChildItem -Path 'C:\Program Files\PostgreSQL' -Filter 'psql.exe' -Recurse -ErrorAction SilentlyContinue)
        if ($candidates.Count -gt 0) { $psql = [string]$candidates[0].FullName }
    }
    if ([string]::IsNullOrWhiteSpace($psql)) { $psql = $null }
}
catch { $psql = $null }

$script:PgReady = $false
$script:PgHost = 'localhost'; $script:PgPort = '5432'; $script:PgDb = ''; $script:PgUser = ''

if ($null -ne $psql) {
    $connectionString = $null
    $secretText = $null
    try {
        $secretText = (& dotnet user-secrets list --project $apiProject 2>&1) -join "`n"
        if ($LASTEXITCODE -eq 0) {
            foreach ($line in @($secretText -split "`n")) {
                $i = $line.IndexOf(' = ')
                if ($i -gt 0 -and $line.Substring(0, $i).Trim() -eq 'Database:ConnectionString') {
                    $connectionString = $line.Substring($i + 3)
                }
            }
        }
    }
    catch { $connectionString = $null }
    finally { $secretText = $null }

    if (-not [string]::IsNullOrWhiteSpace($connectionString)) {
        $parts = @{}
        foreach ($seg in @($connectionString -split ';')) {
            $j = $seg.IndexOf('=')
            if ($j -gt 0) { $parts[$seg.Substring(0, $j).Trim().ToLowerInvariant()] = $seg.Substring($j + 1).Trim() }
        }
        $connectionString = $null

        if ($parts.ContainsKey('host'))     { $script:PgHost = $parts['host'] }
        if ($parts.ContainsKey('port'))     { $script:PgPort = $parts['port'] }
        if ($parts.ContainsKey('database')) { $script:PgDb   = $parts['database'] }
        if ($parts.ContainsKey('username')) { $script:PgUser = $parts['username'] }
        if ($parts.ContainsKey('password')) { $env:PGPASSWORD = $parts['password'] }
        $parts = $null

        $script:PgReady = $true
        Say ('  ' + $script:PgHost + ':' + $script:PgPort + '/' + $script:PgDb + '   (password not shown)')
    }
    else {
        Say '  Database:ConnectionString not available from user-secrets.'
    }
}
else {
    Say '  psql not found.'
}

function Invoke-Sql([string]$sql) {
    if (-not $script:PgReady) { return 'NO DATABASE' }
    $r = & $psql -h $script:PgHost -p $script:PgPort -U $script:PgUser -d $script:PgDb -t -A -F ' | ' -c $sql 2>&1
    if ($LASTEXITCODE -ne 0) { return 'QUERY FAILED' }
    return (($r | Out-String).Trim())
}

function Get-Rows([string]$sql) {
    return @((Invoke-Sql $sql) -split "`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

try {
    # ----------------------------------------------------------------------------------
    # 4. Exactly one enabled watch for AAPL.US / equity-price-review
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 4. EXACTLY ONE ENABLED WATCH FOR AAPL.US / equity-price-review ---'

    $allWatches = Invoke-Sql 'select count(*) from watches'
    $matching = Invoke-Sql ("select count(*) from watches where target_identifier = '$Symbol' " +
                            "and cycle_template = '$Template' and enabled = true")

    Say ('  watches (all)                        : ' + $allWatches)
    Say ('  enabled, AAPL.US, equity-price-review: ' + $matching)
    Say ''
    Say '  rows: id | name | target_kind | target_identifier | trigger_type | capability | template | enabled | cooldown | interval | fire_count | created_at_utc | last_fired_at_utc'
    foreach ($r in (Get-Rows 'select id, name, target_kind, target_identifier, trigger_type, capability, cycle_template, enabled, cooldown, condition_interval, fire_count, created_at_utc, last_fired_at_utc from watches order by created_at_utc')) {
        Say ('    ' + $r.Trim())
    }

    Verdict 4 'Exactly one enabled AAPL.US watch' ($matching -eq '1') ("total watches = $allWatches")

    # ----------------------------------------------------------------------------------
    # 6. operating_cycles is still 0
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 6. operating_cycles IS STILL 0 ---'

    $cycles = Invoke-Sql 'select count(*) from operating_cycles'
    Say ('  operating_cycles : ' + $cycles)
    Verdict 6 'operating_cycles is 0' ($cycles -eq '0')

    # ----------------------------------------------------------------------------------
    # 9. No live EODHD request has been made
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 9. NO LIVE EODHD REQUEST HAS BEEN MADE ---'

    $runs      = Invoke-Sql "select count(*) from ingestion_runs where source_id = '$SourceId'"
    $allRuns   = Invoke-Sql 'select count(*) from ingestion_runs'
    $obs       = Invoke-Sql 'select count(*) from observations'

    Say ('  ingestion_runs for eodhd-eod : ' + $runs + '     <-- the decisive one')
    Say ('  ingestion_runs (all sources) : ' + $allRuns)
    Say ('  observations (all)           : ' + $obs)
    Say ''
    Say '  Every fetch goes through IngestionGateway, which writes a run row BEFORE it calls a'
    Say '  provider and writes one even when it refuses. Zero rows for eodhd-eod therefore means'
    Say '  the connector was never reached - not merely that nothing was stored.'

    Verdict 9 'No EODHD request ever made' ($runs -eq '0') ("ingestion_runs=$runs, observations=$obs")

    # ----------------------------------------------------------------------------------
    # 8. The first schedule boundary
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 8. FIRST SCHEDULE BOUNDARY FOR THE AAPL WATCH ---'

    # Seconds as well as text: extract(epoch ...) is unambiguous, the text is the fallback.
    $row = Invoke-Sql ("select created_at_utc, coalesce(last_fired_at_utc::text, ''), " +
                       "condition_interval::text, max_signal_age::text, " +
                       "coalesce(extract(epoch from condition_interval)::text, ''), " +
                       "coalesce(extract(epoch from max_signal_age)::text, '') " +
                       "from watches where target_identifier = '$Symbol' " +
                       "and cycle_template = '$Template' and enabled = true limit 1")

    if ($row -eq 'QUERY FAILED' -or $row -eq 'NO DATABASE' -or [string]::IsNullOrWhiteSpace($row)) {
        Say ('  could not read the watch row: ' + $row)
        Verdict 8 'First boundary computed' $null
    }
    else {
        $f = @($row -split '\|' | ForEach-Object { $_.Trim() })
        Say ('  created_at_utc     : ' + $f[0])
        Say ('  last_fired_at_utc  : ' + $(if ([string]::IsNullOrWhiteSpace($f[1])) { '(never fired)' } else { $f[1] }))
        Say ('  condition_interval : ' + $f[2])
        Say ('  max_signal_age     : ' + $(if ($f.Count -gt 3) { $f[3] } else { '(not read)' }))

        # ScheduleTicker.DueInstant: since = last fired ?? created; boundary = since + whole intervals.
        $since = ConvertTo-Utc $(if ([string]::IsNullOrWhiteSpace($f[1])) { $f[0] } else { $f[1] })

        $interval = $null
        if ($f.Count -gt 4 -and -not [string]::IsNullOrWhiteSpace($f[4])) {
            $interval = [TimeSpan]::FromSeconds([double]::Parse($f[4], [Globalization.CultureInfo]::InvariantCulture))
        }
        if ($null -eq $interval) { $interval = ConvertTo-TimeSpanFlexible $f[2] }

        $maxAge = $null
        if ($f.Count -gt 5 -and -not [string]::IsNullOrWhiteSpace($f[5])) {
            $maxAge = [TimeSpan]::FromSeconds([double]::Parse($f[5], [Globalization.CultureInfo]::InvariantCulture))
        }

        $nowUtc = (Get-Date).ToUniversalTime()

        if ($null -eq $since -or $null -eq $interval -or $interval -le [TimeSpan]::Zero) {
            Say ''
            Say '  The reference instant or the interval could not be read as a number. Not guessing:'
            Say '  a boundary computed from a misparsed interval would be worse than none.'
            Verdict 8 'First boundary computed' $null 'unparseable'
            $script:BoundaryDone = $true
        }

        if (-not $script:BoundaryDone) {

        $firstBoundary = $since.Add($interval)

        Say ''
        Say ('  reference instant (last fired, else created) : ' + $since.ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
        Say ('  interval                                     : ' + $interval.ToString())
        Say ('  now (UTC)                                    : ' + $nowUtc.ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
        Say ''
        Say ('  FIRST BOUNDARY (UTC)   : ' + $firstBoundary.ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
        Say ('  FIRST BOUNDARY (local) : ' + $firstBoundary.ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss'))

        if ($nowUtc -lt $firstBoundary) {
            $wait = $firstBoundary - $nowUtc
            Say ('  time until it          : ' + [string]::Format([Globalization.CultureInfo]::InvariantCulture,
                 '{0}h {1}m', [int]$wait.TotalHours, $wait.Minutes))
            Say '  The watch is NOT yet due. Enabling RunCycles before this instant starts nothing.'
        }
        else {
            $elapsed = $nowUtc - $since
            $whole = [Math]::Floor($elapsed.Ticks / $interval.Ticks)
            $current = $since.AddTicks([long]$whole * $interval.Ticks)
            $age = $nowUtc - $current

            Say ('  CURRENT BOUNDARY (UTC) : ' + $current.ToString('yyyy-MM-dd HH:mm:ss') + 'Z')
            Say ('  its age right now      : ' + [string]::Format([Globalization.CultureInfo]::InvariantCulture,
                 '{0}h {1}m', [int]$age.TotalHours, $age.Minutes))
            Say '  The watch IS due. Whether it fires depends on max_signal_age above: a boundary'
            Say '  older than that is refused as stale, and the next one arrives one interval later.'
        }

        if ($null -ne $maxAge) {
            Say ('  max_signal_age         : ' + $maxAge.ToString() + '  (a boundary older than this is refused)')
        }

        Verdict 8 'First boundary computed' $true ($firstBoundary.ToString('yyyy-MM-dd HH:mm:ss') + 'Z')

        }
    }
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

# --------------------------------------------------------------------------------------
# 7. Raw archive writability
# --------------------------------------------------------------------------------------
Say ''
Say '--- 7. RAW ARCHIVE IS WRITABLE ---'

$archiveOk = $null
try {
    $rootPath = $null
    foreach ($file in @($devSettings, (Join-Path $apiProject 'appsettings.json'))) {
        if ($null -ne $rootPath -or -not (Test-Path -LiteralPath $file)) { continue }
        $json = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        $section = Get-Prop $json 'RawArchive' $null
        $value = Get-Prop $section 'RootPath' $null
        if ($null -ne $value) { $rootPath = [string]$value }
    }

    if ($null -eq $rootPath) {
        Say '  RawArchive:RootPath could not be read.'
    }
    else {
        $resolved = if ([System.IO.Path]::IsPathRooted($rootPath)) { $rootPath }
                    else { Join-Path $apiProject $rootPath }

        Say ('  configured : ' + $rootPath)
        Say ('  resolved   : ' + $resolved)
        Say ('  exists     : ' + (Test-Path -LiteralPath $resolved -PathType Container))

        # FileSystemRawResponseArchive calls Directory.CreateDirectory per payload, so a
        # missing folder is fine provided the parent is writable.
        $probeDir = if (Test-Path -LiteralPath $resolved -PathType Container) { $resolved }
                    else { Split-Path -Parent $resolved }

        if (Test-Path -LiteralPath $probeDir -PathType Container) {
            $probe = Join-Path $probeDir ('.write-probe-' + [Guid]::NewGuid().ToString('N') + '.tmp')
            [System.IO.File]::WriteAllBytes($probe, [byte[]]::new(0))
            $archiveOk = Test-Path -LiteralPath $probe
            Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
            Say ('  probed     : ' + $probeDir)
            Say ('  writable   : ' + $archiveOk + '   (one zero-byte probe file, created and deleted)')
        }
        else {
            $archiveOk = $false
            Say '  Neither the archive folder nor its parent exists.'
        }

        $payloads = 0
        if (Test-Path -LiteralPath $resolved -PathType Container) {
            $payloads = @(Get-ChildItem -LiteralPath $resolved -Recurse -File -ErrorAction SilentlyContinue).Count
        }
        Say ('  archived payload files present : ' + $payloads + '   (0 corroborates check 9)')
    }
}
catch {
    $archiveOk = $false
    Say ('  FAILED : ' + $_.Exception.Message)
}

Say '  NOTE: probed as the account running this script. If the API runs as a different'
Say '  user, its own access may differ.'
Verdict 7 'Raw archive writable' $archiveOk

# --------------------------------------------------------------------------------------
# 2. Operator privileges AS LOADED BY THE RUNNING API
# --------------------------------------------------------------------------------------
Say ''
Say '--- 2. OPERATOR PRIVILEGES LOADED AFTER THE RESTART ---'

$privilegesOk = $null

Write-Host ''
Write-Host 'Enter the operator key to prove what the RUNNING API loaded (input hidden).' -ForegroundColor Yellow
Write-Host 'Press Enter alone to skip; the check then falls back to the configured shape.' -ForegroundColor Yellow
$secure = Read-Host -AsSecureString

$key = $null
if ($null -ne $secure) {
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $key = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$expected = @('AdministerWatches', 'AnswerEscalations', 'ViewPortfolio')

if (-not [string]::IsNullOrWhiteSpace($key)) {
    try {
        $me = Get-First (Invoke-RestMethod -Uri ($BaseUrl + '/api/operator/whoami') `
            -Headers @{ 'X-Operator-Key' = $key } -TimeoutSec 30)

        $privs = @(@(Get-Prop $me 'privileges' @()) | Sort-Object)

        Say '  source of truth: GET /api/operator/whoami on the running process.'
        Say ('  operator id  : ' + (Show (Get-Prop $me 'operatorId' (Get-Prop $me 'id'))))
        Say ('  privileges   : ' + $(if ($privs.Count -eq 0) { '(none)' } else { $privs -join ', ' }))

        $privilegesOk = ($privs.Count -eq 3) -and
                        (@(Compare-Object $expected $privs -SyncWindow 0).Count -eq 0)

        Say ('  exactly the three expected : ' + $privilegesOk)
    }
    catch {
        Say ('  whoami FAILED : ' + $_.Exception.Message)
        Say '  The key was not accepted, or the API is not reachable.'
        $privilegesOk = $false
    }
    finally {
        $key = $null
        [System.GC]::Collect()
    }
}
else {
    Say '  Skipped. Falling back to the CONFIGURED shape, which does not prove the running'
    Say '  process reloaded it.'

    $map = @{}
    $text = $null
    try {
        $text = (& dotnet user-secrets list --project $apiProject 2>&1) -join "`n"
        if ($LASTEXITCODE -eq 0) {
            foreach ($line in @($text -split "`n")) {
                $i = $line.IndexOf(' = ')
                if ($i -gt 0) { $map[$line.Substring(0, $i).Trim()] = $line.Substring($i + 3) }
            }
        }
    }
    catch { }
    finally { $text = $null }

    $configured = @()
    foreach ($k in @($map.Keys)) {
        if ($k -match '^Operators:Accounts:\d+:Privileges:\d+$') { $configured += $map[$k] }
    }
    $configured = @($configured | Sort-Object)
    $map = $null

    Say ('  configured privileges : ' + $(if ($configured.Count -eq 0) { '(none)' } else { $configured -join ', ' }))

    $matches = ($configured.Count -eq 3) -and
               (@(Compare-Object $expected $configured -SyncWindow 0).Count -eq 0)

    Say ('  configuration matches the three expected : ' + $matches)
    Say '  NOT PROOF the running API loaded them. Re-run and supply the key for that.'
    $privilegesOk = $null
}

Verdict 2 'Operator privileges loaded by running API' $privilegesOk

# --------------------------------------------------------------------------------------
Say ''
Say '=== SUMMARY ==='
foreach ($v in $verdicts | Sort-Object) { Say $v }

Say ''
Say '=== END. NOTHING WAS CHANGED. ==='
Say 'No code, configuration or User Secrets modified. No watch created or disabled.'
Say 'RunCycles untouched. No cycle started. No EODHD request made.'

Save-Log
Write-Host ''
Write-Host ('Written: ' + $log)
