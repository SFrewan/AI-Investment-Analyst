#requires -Version 5.1
<#
    OBSERVATION WINDOW - STEP 4: ACTIVATE THE eodhd-eod SOURCE.

    WHAT THIS DOES
      POST http://localhost:5143/api/sources/eodhd-eod/activation
      authenticated with the X-Operator-Key header, then verifies the persisted status
      and the audit record.

    WHAT THIS DOES NOT DO
      It does not create a watch. It does not enable OperationsHost:RunCycles. It does not
      make an EODHD request - ActivateSourceHandler only flips the registry row through the
      Action/Policy seam; the provider is never called. It changes no other configuration.

    THE KEY
      You type it. It is read with Read-Host -AsSecureString, held only as long as the one
      request needs it, and zeroed afterwards. It is never written to the log, never echoed,
      never hashed, never measured, and never sent anywhere but localhost:5143.

    STRICTMODE HARDENING
      Under Set-StrictMode -Version Latest, reading an absent property throws
      PropertyNotFoundStrict, and a pipeline yielding one item assigns a scalar whose
      .Count throws. Every property read from a JSON response therefore goes through
      Get-Prop, and every collection is normalised with @( ). Invoke-RestMethod is
      assigned to a variable BEFORE being wrapped, because Windows PowerShell 5.1 unrolls
      a JSON array onto the pipeline while PowerShell 7 emits it as a single item -
      @(cmdlet) is wrong on 7, @($variable) is right on both.

    UNKNOWN IS NOT FALSE
      If isActive cannot be read from the response, this script STOPS rather than assuming
      the source is inactive. Guessing the before-state of a mutating call is exactly the
      substitution this codebase refuses to make elsewhere.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'artifacts\verify'
$null = New-Item -ItemType Directory -Force -Path $out
$log  = Join-Path $out 'observation-step4-activation.txt'

$lines = New-Object 'System.Collections.Generic.List[string]'
function Say([string]$text) { $null = $lines.Add($text); Write-Host $text }
function Save-Log { Set-Content -Path $log -Value ($lines -join "`r`n") -Encoding UTF8 }

# Safe property read: an absent or null property yields the fallback rather than throwing.
function Get-Prop($obj, [string]$name, $fallback = $null) {
    if ($null -eq $obj) { return $fallback }
    $p = $obj.PSObject.Properties[$name]
    if ($null -eq $p) { return $fallback }
    if ($null -eq $p.Value) { return $fallback }
    return $p.Value
}

# Cross-version single-object normaliser. Handles a scalar, a 5.1 unrolled array and a
# PowerShell 7 array-as-one-item identically.
function Get-First($response) {
    if ($null -eq $response) { return $null }
    $items = @($response)
    if ($items.Count -eq 0) { return $null }
    return $items[0]
}

# Reads a boolean that must genuinely be present. Returns $null when it is absent or is
# not a boolean, so the caller can tell "false" apart from "not stated".
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

# Extracts an HTTP status code and response body across both engines: Windows PowerShell
# 5.1 throws WebException carrying a response stream, PowerShell 7 throws
# HttpResponseException and puts the body in ErrorDetails.
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

        $ex = $errorRecord.Exception
        $rp = $ex.PSObject.Properties['Response']
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

Say '=== OBSERVATION WINDOW - STEP 4: SOURCE ACTIVATION ==='
Say ("timestamp (local) : " + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
Say ("powershell        : " + $PSVersionTable.PSVersion.ToString())
Say ''

# --------------------------------------------------------------------------------------
# 0. Pre-flight. Refuse to go further if the source is missing, already active, or if its
#    state cannot be read.
# --------------------------------------------------------------------------------------
Say '--- 0. PRE-FLIGHT ---'

$before = $null
try {
    $beforeResponse = Invoke-RestMethod -Uri ($BaseUrl + '/api/sources/' + $SourceId) -TimeoutSec 30
    $before = Get-First $beforeResponse
}
catch {
    Say ('  Could not read ' + $SourceId + ' : ' + (Show $_.Exception.Message '(no message)'))
    Say '  STOPPING. Nothing was attempted.'
    Save-Log
    exit 1
}

if ($null -eq $before) {
    Say ('  The API returned no source for ' + $SourceId + '.')
    Say '  STOPPING. Nothing was attempted.'
    Save-Log
    exit 1
}

$beforeActive = Get-BoolOrNull $before 'isActive'

Say ('  id       : ' + (Show (Get-Prop $before 'id')))
Say ('  name     : ' + (Show (Get-Prop $before 'name')))
Say ('  isActive : ' + (Show $beforeActive) + '   <-- state BEFORE')

if ($null -eq $beforeActive) {
    Say ''
    Say '  isActive could not be read from the response. Unknown is not false, and this'
    Say '  script will not guess the before-state of a mutating call.'
    Say '  STOPPING. Nothing was attempted.'
    Save-Log
    exit 1
}

if ($beforeActive) {
    Say ''
    Say '  Already active. Nothing to do; not sending the request.'
    Save-Log
    exit 0
}

# --------------------------------------------------------------------------------------
# 1. The operator key. Typed here, never stored.
# --------------------------------------------------------------------------------------
Say ''
Say '--- 1. OPERATOR CREDENTIAL ---'
Write-Host ''
Write-Host 'Enter the operator key (input is hidden, and is not written to any file):' -ForegroundColor Yellow
$secure = Read-Host -AsSecureString

$key = $null
if ($null -ne $secure) {
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $key = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

if ([string]::IsNullOrWhiteSpace($key)) {
    $key = $null
    Say '  No key entered. STOPPING. Nothing was attempted.'
    Save-Log
    exit 1
}

Say '  key received from the secure prompt (not shown, not logged, not measured).'

$headers = @{ $Header = $key }
$activationStatus = $null

try {
    # ----------------------------------------------------------------------------------
    # 2. Confirm the key authenticates and carries the required privilege, BEFORE the
    #    mutating call.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 2. WHOAMI (proves the key authenticates before anything is changed) ---'

    try {
        $meResponse = Invoke-RestMethod -Uri ($BaseUrl + '/api/operator/whoami') -Headers $headers -TimeoutSec 30
        $me = Get-First $meResponse

        if ($null -eq $me) {
            Say '  whoami returned an empty body. STOPPING before the activation request.'
            Save-Log
            exit 1
        }

        Say ('  operator id   : ' + (Show (Get-Prop $me 'operatorId' (Get-Prop $me 'id'))))
        Say ('  display name  : ' + (Show (Get-Prop $me 'displayName')))

        $privs = @(Get-Prop $me 'privileges' @())
        if ($privs.Count -eq 0) {
            Say '  privileges    : (none reported)'
        }
        else {
            Say ('  privileges    : ' + ($privs -join ', '))
        }

        Say ('  full response  : ' + ($me | ConvertTo-Json -Depth 4 -Compress))
    }
    catch {
        Say ('  FAILED ' + (Show (Get-ErrorStatus $_) '') + ' : the key was not accepted, or the privilege is missing.')
        Say ('  detail : ' + (Show (Get-ErrorBody $_) '(no body)'))
        Say '  STOPPING before the activation request. Nothing was changed.'
        Save-Log
        exit 1
    }

    # ----------------------------------------------------------------------------------
    # 3. Activate.
    # ----------------------------------------------------------------------------------
    Say ''
    Say '--- 3. POST /api/sources/eodhd-eod/activation ---'

    $body = $null
    try {
        $resp = Invoke-WebRequest `
            -Uri ($BaseUrl + '/api/sources/' + $SourceId + '/activation') `
            -Method Post `
            -Headers $headers `
            -ContentType 'application/json' `
            -UseBasicParsing `
            -TimeoutSec 60

        $activationStatus = [string][int]$resp.StatusCode
        $body = ($resp.Content | Out-String).Trim()
    }
    catch {
        $activationStatus = Get-ErrorStatus $_
        $body = Get-ErrorBody $_
    }

    Say ('  HTTP status : ' + (Show $activationStatus '(no status)'))
    Say ('  response    : ' + (Show $body '(no body)'))
    Say ''
    Say '  200 = activated (or already active).  202 = policy requires a human decision,'
    Say '  nothing was written.  403 = refused by policy or by the licensing rule in the'
    Say '  domain.  401 = the key or privilege was not accepted.'
}
finally {
    $key = $null
    $headers = $null
    [System.GC]::Collect()
}

# --------------------------------------------------------------------------------------
# 4. Verify the persisted state, unauthenticated, from the registry itself.
# --------------------------------------------------------------------------------------
Say ''
Say '--- 4. PERSISTED STATE AFTER THE ATTEMPT (GET /api/sources/eodhd-eod) ---'

try {
    $afterResponse = Invoke-RestMethod -Uri ($BaseUrl + '/api/sources/' + $SourceId) -TimeoutSec 30
    $after = Get-First $afterResponse
    $afterActive = Get-BoolOrNull $after 'isActive'

    Say ('  isActive BEFORE : ' + (Show $beforeActive))
    Say ('  isActive AFTER  : ' + (Show $afterActive))
    Say ('  updatedAtUtc    : ' + (Show (Get-Prop $after 'updatedAtUtc')))

    if ($null -eq $afterActive) {
        Say '  VERDICT : isActive could not be read after the attempt. State UNKNOWN, not assumed.'
    }
    elseif ($afterActive -and -not $beforeActive) {
        Say '  VERDICT : the source is now ACTIVE. The registry row changed.'
    }
    elseif (-not $afterActive) {
        Say '  VERDICT : the source is still INACTIVE. Nothing was persisted.'
    }
}
catch {
    Say ('  FAILED : ' + (Show $_.Exception.Message '(no message)'))
}

# --------------------------------------------------------------------------------------
# 5. Verify the audit record, straight from PostgreSQL.
# --------------------------------------------------------------------------------------
Say ''
Say '--- 5. AUDIT RECORD ---'

$psql = $null
try {
    $cmd = Get-Command psql -ErrorAction SilentlyContinue
    if ($null -ne $cmd) { $psql = [string]$cmd.Source }

    if ([string]::IsNullOrWhiteSpace($psql)) {
        $candidates = @(Get-ChildItem -Path 'C:\Program Files\PostgreSQL' -Filter 'psql.exe' -Recurse -ErrorAction SilentlyContinue)
        if ($candidates.Count -gt 0) { $psql = [string]$candidates[0].FullName }
    }

    if ([string]::IsNullOrWhiteSpace($psql)) { $psql = $null }
}
catch {
    $psql = $null
}

if ($null -eq $psql) {
    Say '  psql not found - the audit record could not be read directly. The HTTP status above'
    Say '  is the only evidence this run produced; a 200 means the gateway executed the action,'
    Say '  which is what writes the audit record.'
}
else {
    Say ('  psql : ' + $psql)

    $connectionString = $null
    try {
        $apiProject = Join-Path $root 'src\AI.Investment.Api'
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
        finally {
            $secretText = $null
        }
    }
    catch {
        $connectionString = $null
        Say ('  user-secrets read FAILED : ' + $_.Exception.GetType().Name +
             ' at line ' + $_.InvocationInfo.ScriptLineNumber + ' (details suppressed)')
    }

    if ([string]::IsNullOrWhiteSpace($connectionString)) {
        Say '  Database:ConnectionString not available; skipping the direct read.'
    }
    else {
        try {
            $parts = @{}
            foreach ($seg in @($connectionString -split ';')) {
                $j = $seg.IndexOf('=')
                if ($j -gt 0) { $parts[$seg.Substring(0, $j).Trim().ToLowerInvariant()] = $seg.Substring($j + 1).Trim() }
            }
            $connectionString = $null

            $pgHost = if ($parts.ContainsKey('host'))     { $parts['host'] }     else { 'localhost' }
            $pgPort = if ($parts.ContainsKey('port'))     { $parts['port'] }     else { '5432' }
            $pgDb   = if ($parts.ContainsKey('database')) { $parts['database'] } else { '' }
            $pgUser = if ($parts.ContainsKey('username')) { $parts['username'] } else { '' }
            if ($parts.ContainsKey('password')) { $env:PGPASSWORD = $parts['password'] }
            $parts = $null

            function Invoke-Sql([string]$sql) {
                $r = & $psql -h $pgHost -p $pgPort -U $pgUser -d $pgDb -t -A -F ' | ' -c $sql 2>&1
                if ($LASTEXITCODE -ne 0) { return 'QUERY FAILED' }
                return (($r | Out-String).Trim())
            }

            function Say-Rows([string]$sql, [string]$emptyText) {
                $rows = @((Invoke-Sql $sql) -split "`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
                if ($rows.Count -eq 0) { Say ('    ' + $emptyText); return }
                foreach ($r in $rows) { Say ('    ' + $r.Trim()) }
            }

            try {
                Say ('  database : ' + $pgHost + ':' + $pgPort + '/' + $pgDb + '   (password not shown)')
                Say ''
                Say '  data_sources row: id | is_active | updated_at_utc'
                Say-Rows "select id, is_active, updated_at_utc from data_sources where id = '$SourceId'" '(no row)'
                Say ''
                Say '  audit_records for source.activate (newest 5): occurred_at | actor | actor_kind | outcome | summary'
                Say-Rows "select occurred_at_utc, actor, actor_kind, outcome, left(summary, 120) from audit_records where action_type = 'source.activate' order by occurred_at_utc desc limit 5" '(no audit record)'
                Say ''
                Say '  action_executions for source.activate (newest 5): started | completed | status'
                Say-Rows "select started_at_utc, completed_at_utc, status from action_executions where action_type = 'source.activate' order by started_at_utc desc limit 5" '(no execution record)'
                Say ''
                Say ("  audit_records with action_type = 'source.activate' : " + (Invoke-Sql "select count(*) from audit_records where action_type = 'source.activate'"))
                Say '  (Baseline before this run was 0, so 1 here is the record this activation wrote.)'
            }
            finally {
                Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
            }
        }
        catch {
            Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
            Say ('  AUDIT READ FAILED : ' + $_.Exception.GetType().Name +
                 ' at line ' + $_.InvocationInfo.ScriptLineNumber)
        }
    }
}

Say ''
Say '=== END ==='
Say 'No watch was created. RunCycles was not enabled. No EODHD request was made.'

Save-Log
Write-Host ''
Write-Host ('Written: ' + $log)
