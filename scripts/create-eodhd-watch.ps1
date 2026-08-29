#requires -Version 5.1
<#
    OBSERVATION WINDOW - STEP 5: CREATE THE FIRST MINIMAL MARKET-DATA WATCH.

    WHAT THIS DOES
      POST http://localhost:5143/api/operator/watches
      authenticated with the X-Operator-Key header, creating ONE scheduled watch:

        name             : AAPL daily review
        targetKind       : Security
        targetIdentifier : AAPL.US          (TICKER.EXCHANGE, exactly one dot)
        intervalMinutes  : 1440             (once a day)
        cooldownMinutes  : 240              (domain minimum is 30 seconds)
        capability       : OpportunityManagement
        cycleTemplate    : equity-price-review

      One instrument. The smallest deterministic set that proves the pipeline.

    WHAT THIS DOES NOT DO
      No EODHD request. OperatorConsole.CreateScheduledWatchAsync builds the Watch
      aggregate in memory and persists it inside the Action/Policy seam; no provider,
      HTTP client or normalizer is touched. It does not enable RunCycles, does not start
      a cycle, does not generate an opportunity, does not execute a trade, does not
      create a position and does not reach a broker or a venue.

    REVERSIBILITY
      This is reversible as of the watch-disablement block:
        POST /api/operator/watches/{id}/disablement   (AdministerWatches, states a reason)
      routes Watch.Disable through the same Action/Policy seam, and the trigger evaluator
      only ever asks the store for ENABLED watches. Disable, not delete: the row, its
      reason and its firing history survive, so the record of what ran stays answerable.
      The second containment is that OperationsHost:RunCycles is false, so the watch
      cannot fire at all yet. This script verifies that before it asks for anything and
      refuses if it is not false. You are still asked to confirm explicitly.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log  = Join-Path $out 'observation-step5-watch.txt'

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

function Show([object]$value, [string]$whenNull = '(not stated)') {
    if ($null -eq $value) { return $whenNull }
    return [string]$value
}

function Get-ErrorStatus($errorRecord) {
    try {
        $ex = $errorRecord.Exception
        if ($null -eq $ex) { return $null }
        $rp = $ex.PSObject.Properties['Response']
        if ($null -eq $rp -or $null -eq $rp.Value) { return $null }
        $sc = $rp.Value.PSObject.Properties['StatusCode']
        if ($null -eq $sc -or $null -eq $sc.Value) { return $null }
        return [string][int]$sc.Value
    }
    catch { return $null }
}

function Get-ErrorBody($errorRecord) {
    try {
        $details = $errorRecord.PSObject.Properties['ErrorDetails']
        if ($null -ne $details -and $null -ne $details.Value) {
            $m = $details.Value.PSObject.Properties['Message']
            if ($null -ne $m -and -not [string]::IsNullOrWhiteSpace([string]$m.Value)) {
                return ([string]$m.Value).Trim()
            }
        }
        $rp = $errorRecord.Exception.PSObject.Properties['Response']
        if ($null -ne $rp -and $null -ne $rp.Value) {
            $stream = $rp.Value.GetResponseStream()
            if ($null -ne $stream) {
                $reader = New-Object System.IO.StreamReader($stream)
                try { return $reader.ReadToEnd().Trim() } finally { $reader.Dispose() }
            }
        }
        return $errorRecord.Exception.Message
    }
    catch { return $errorRecord.Exception.Message }
}

$BaseUrl  = 'http://localhost:5143'
$SourceId = 'eodhd-eod'
$Header   = 'X-Operator-Key'

# The watch, stated once.
$Watch = [ordered]@{
    name             = 'AAPL daily review'
    targetKind       = 'Security'
    targetIdentifier = 'AAPL.US'
    intervalMinutes  = 1440
    cooldownMinutes  = 240
    capability       = 'OpportunityManagement'
    cycleTemplate    = 'equity-price-review'
}

Say '=== OBSERVATION WINDOW - STEP 5: CREATE THE FIRST MARKET-DATA WATCH ==='
Say ("timestamp (local) : " + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
Say ("powershell        : " + $PSVersionTable.PSVersion.ToString())
Say ''

# --------------------------------------------------------------------------------------
# 1. Pre-flight. Every check is read-only and happens before the key is requested.
# --------------------------------------------------------------------------------------
Say '--- 1. PRE-FLIGHT (read-only) ---'

# 1a. The source must exist and be active.
try {
    $source = Get-First (Invoke-RestMethod -Uri ($BaseUrl + '/api/sources/' + $SourceId) -TimeoutSec 30)
}
catch {
    Say ('  Could not read ' + $SourceId + ' : ' + (Show $_.Exception.Message '(no message)'))
    Say '  STOPPING. Nothing was attempted.'
    Save-Log; exit 1
}

$sourceActive = Get-BoolOrNull $source 'isActive'
Say ('  source ' + $SourceId + ' isActive : ' + (Show $sourceActive))

if ($sourceActive -ne $true) {
    Say '  The source is not active (or its state could not be read). A watch pointed at an'
    Say '  inactive source would observe nothing. STOPPING. Nothing was attempted.'
    Save-Log; exit 1
}

# 1b. RunCycles must be false. This is the containment that makes the watch inert.
$devSettings = Join-Path $root 'src\AI.Investment.Api\appsettings.Development.json'
$runCycles = $null
try {
    if (Test-Path -LiteralPath $devSettings) {
        $json = Get-Content -LiteralPath $devSettings -Raw | ConvertFrom-Json
        $hostSection = Get-Prop $json 'OperationsHost' $null
        $runCycles = Get-BoolOrNull $hostSection 'RunCycles'
    }
}
catch { $runCycles = $null }

Say ('  OperationsHost:RunCycles (appsettings.Development.json) : ' + (Show $runCycles))

if ($runCycles -ne $false) {
    Say '  RunCycles is not demonstrably false. This script will not create a watch that could'
    Say '  begin firing immediately. STOPPING. Nothing was attempted.'
    Save-Log; exit 1
}

# 1c. Existing watches, straight from the database.
$psql = $null
try {
    # Application only: a function or alias named psql resolves first but has no Source,
    # which would silently yield an empty path.
    $cmd = @(Get-Command psql -CommandType Application -ErrorAction SilentlyContinue) |
        Select-Object -First 1
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

function Connect-Pg {
    if ($null -eq $psql) { return $false }
    $connectionString = $null
    $secretText = $null
    try {
        $apiProject = Join-Path $root 'src\AI.Investment.Api'
        $secretText = (& dotnet user-secrets list --project $apiProject 2>&1) -join "`n"
        if ($LASTEXITCODE -ne 0) { return $false }
        foreach ($line in @($secretText -split "`n")) {
            $i = $line.IndexOf(' = ')
            if ($i -gt 0 -and $line.Substring(0, $i).Trim() -eq 'Database:ConnectionString') {
                $connectionString = $line.Substring($i + 3)
            }
        }
    }
    catch { return $false }
    finally { $secretText = $null }

    if ([string]::IsNullOrWhiteSpace($connectionString)) { return $false }

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
    return $true
}

function Invoke-Sql([string]$sql) {
    if (-not $script:PgReady) { return 'NO DATABASE' }
    $r = & $psql -h $script:PgHost -p $script:PgPort -U $script:PgUser -d $script:PgDb -t -A -F ' | ' -c $sql 2>&1
    if ($LASTEXITCODE -ne 0) { return 'QUERY FAILED' }
    return (($r | Out-String).Trim())
}

function Say-Rows([string]$sql, [string]$emptyText) {
    $rows = @((Invoke-Sql $sql) -split "`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($rows.Count -eq 0) { Say ('    ' + $emptyText); return }
    foreach ($r in $rows) { Say ('    ' + $r.Trim()) }
}

$null = Connect-Pg

if (-not $script:PgReady) {
    Say '  psql or the connection string is unavailable, so existing watches cannot be checked'
    Say '  and persistence could not be verified afterwards. STOPPING. Nothing was attempted.'
    Save-Log; exit 1
}

Say ('  database : ' + $script:PgHost + ':' + $script:PgPort + '/' + $script:PgDb + '   (password not shown)')

$watchCount = Invoke-Sql 'select count(*) from watches'
Say ('  existing watches : ' + $watchCount)

$duplicate = Invoke-Sql ("select count(*) from watches where target_identifier = '" + $Watch.targetIdentifier +
                         "' and cycle_template = '" + $Watch.cycleTemplate + "'")
Say ('  existing watch on ' + $Watch.targetIdentifier + ' / ' + $Watch.cycleTemplate + ' : ' + $duplicate)

if ($duplicate -eq '0') {
    Say '  No existing watch for this instrument and template. Clear to create one.'
}
elseif ($duplicate -match '^\d+$') {
    Say '  A watch for this instrument and template already exists. Not creating a second one.'
    Say '  STOPPING. Nothing was attempted.'
    Save-Log; Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue; exit 0
}
else {
    # Neither zero nor a count: the query did not answer. Refusing is the only safe reading -
    # creating a watch because a check failed is how you end up with two.
    Say '  The duplicate check did not return a count, so it is unknown whether this watch'
    Say '  already exists. Unknown is not zero. STOPPING. Nothing was attempted.'
    Save-Log; Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue; exit 1
}

# --------------------------------------------------------------------------------------
# 2. Exactly what will change, then an explicit confirmation.
# --------------------------------------------------------------------------------------
Say ''
Say '--- 2. WHAT WILL CHANGE ---'
Say '  ONE row inserted into "watches", through the Action/Policy seam, with:'
foreach ($k in $Watch.Keys) { Say ('    ' + ([string]$k).PadRight(18) + ' : ' + $Watch[$k]) }
Say ''
Say '  Also written by the seam: one action_executions row and one audit_records row'
Say '  for action type operator.create-watch, recording the operator who asked.'
Say ''
Say '  NOTHING ELSE CHANGES. RunCycles stays false. No cycle starts. No opportunity is'
Say '  generated. No EODHD request is made. No trade, position, broker or venue is touched.'
Say ''
Say '  REVERSIBLE: POST /api/operator/watches/{id}/disablement switches this watch off'
Say '  through the same seam. RunCycles = false, verified above, is the second'

Write-Host ''
Write-Host 'Type CREATE to proceed, or anything else to abort:' -ForegroundColor Yellow
$confirmation = Read-Host

if ([string]$confirmation -cne 'CREATE') {
    Say ''
    Say '  Not confirmed. STOPPING. Nothing was attempted.'
    Save-Log; Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue; exit 0
}

Say ''
Say '  Confirmed by the operator at the console.'

# --------------------------------------------------------------------------------------
# 3. The operator key.
# --------------------------------------------------------------------------------------
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
    Say '  No key entered. STOPPING. Nothing was attempted.'
    Save-Log; Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue; exit 1
}

Say '  key received from the secure prompt (not shown, not logged, not measured).'

$headers = @{ $Header = $key }

try {
    # ----------------------------------------------------------------------------------
    # 4. Prove the key authenticates BEFORE the mutating call.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 4. WHOAMI (before anything is changed) ---'
    try {
        $me = Get-First (Invoke-RestMethod -Uri ($BaseUrl + '/api/operator/whoami') -Headers $headers -TimeoutSec 30)
        if ($null -eq $me) {
            Say '  whoami returned an empty body. STOPPING before the create request.'
            Save-Log; Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue; exit 1
        }
        Say ('  operator id  : ' + (Show (Get-Prop $me 'operatorId' (Get-Prop $me 'id'))))
        $privs = @(Get-Prop $me 'privileges' @())
        Say ('  privileges   : ' + $(if ($privs.Count -eq 0) { '(none reported)' } else { $privs -join ', ' }))
    }
    catch {
        Say ('  FAILED ' + (Show (Get-ErrorStatus $_) '') + ' : the key was not accepted, or AdministerWatches is missing.')
        Say ('  detail : ' + (Show (Get-ErrorBody $_) '(no body)'))
        Say '  STOPPING before the create request. Nothing was changed.'
        Save-Log; Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue; exit 1
    }

    # ----------------------------------------------------------------------------------
    # 5. Create.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 5. POST /api/operator/watches ---'

    $status = $null; $body = $null
    try {
        $resp = Invoke-WebRequest `
            -Uri ($BaseUrl + '/api/operator/watches') `
            -Method Post `
            -Headers $headers `
            -ContentType 'application/json' `
            -Body ($Watch | ConvertTo-Json -Depth 3) `
            -UseBasicParsing `
            -TimeoutSec 60
        $status = [string][int]$resp.StatusCode
        $body   = ($resp.Content | Out-String).Trim()
    }
    catch {
        $status = Get-ErrorStatus $_
        $body   = Get-ErrorBody $_
    }

    Say ('  HTTP status : ' + (Show $status '(no status)'))
    Say ('  response    : ' + (Show $body '(no body)'))
    Say ''
    Say '  200 Done = created and audited.  400 = the domain refused the definition.'
    Say '  401 = key not accepted.  403 = privilege missing.  409 = policy denied or'
    Say '  approval required; in that case nothing was written.'
}
finally {
    $key = $null
    $headers = $null
    [System.GC]::Collect()
}

# --------------------------------------------------------------------------------------
# 6. Verify persistence from the database.
# --------------------------------------------------------------------------------------
Say ''
Say '--- 6. PERSISTED STATE ---'
try {
    Say ('  watches total : ' + (Invoke-Sql 'select count(*) from watches'))
    Say ''
    Say '  watches rows: name | target_kind | target_identifier | trigger_type | capability | cycle_template | enabled | cooldown | condition_interval | fire_count'
    Say-Rows 'select name, target_kind, target_identifier, trigger_type, capability, cycle_template, enabled, cooldown, condition_interval, fire_count from watches order by created_at_utc' '(no watch rows)'
    Say ''
    Say '  audit_records for operator.create-watch: occurred_at | actor | actor_kind | outcome'
    Say-Rows "select occurred_at_utc, actor, actor_kind, outcome from audit_records where action_type = 'operator.create-watch' order by occurred_at_utc desc limit 5" '(no audit record)'
    Say ''
    Say '  action_executions for operator.create-watch: started | completed | status'
    Say-Rows "select started_at_utc, completed_at_utc, status from action_executions where action_type = 'operator.create-watch' order by started_at_utc desc limit 5" '(no execution record)'
    Say ''
    Say ('  operating_cycles total (must be unchanged; no cycle may have started) : ' + (Invoke-Sql 'select count(*) from operating_cycles'))
}
catch {
    Say ('  VERIFICATION FAILED : ' + $_.Exception.GetType().Name + ' at line ' + $_.InvocationInfo.ScriptLineNumber)
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

# --------------------------------------------------------------------------------------
# 7. Re-assert what did not change.
# --------------------------------------------------------------------------------------
Say ''
Say '--- 7. UNCHANGED ---'
try {
    $json = Get-Content -LiteralPath $devSettings -Raw | ConvertFrom-Json
    Say ('  OperationsHost:RunCycles still : ' + (Show (Get-BoolOrNull (Get-Prop $json 'OperationsHost' $null) 'RunCycles')))
}
catch {
    Say '  OperationsHost:RunCycles could not be re-read.'
}
Say '  No EODHD request was made. No cycle was started. No opportunity was generated.'
Say '  No trade, position, broker call, L4 activation or real-money action occurred.'
Say ''
Say '  To reverse this: POST /api/operator/watches/{id}/disablement with a reason,'
Say '  authenticated with the same operator key. The watch id is in the row above.'

Say ''
Say '=== END ==='
Save-Log
Write-Host ''
Write-Host ('Written: ' + $log)
